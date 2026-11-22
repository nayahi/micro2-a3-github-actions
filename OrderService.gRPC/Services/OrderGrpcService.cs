using ECommerceGRPC.Orders;
using ECommerceGRPC.Products;
using ECommerceGRPC.Users;
using Grpc.Core;
using OrderService.gRPC.Mensajeria;
using OrderService.gRPC.Saga;

namespace OrderService.gRPC.Services;

// Clase 2: valida un pedido llamando a Usuarios y a Productos (servidor y cliente gRPC a la vez).
// Clase 3: además lo CREA y publica OrderCreated. La parte sincrónica (validar) responde al cliente;
// la parte asíncrona (notificar, procesar) ocurre después, en otros servicios, sin que el cliente espere.
// Clase 4: el pedido nace PENDIENTE en el registro de la saga; OrquestadorSaga lo termina (CONFIRMADO o CANCELADO).
public class OrderGrpcService(
    UserService.UserServiceClient usuarios,
    ProductService.ProductServiceClient productos,
    PublicadorPedidos publicador,
    RegistroPedidos registro,
    ILogger<OrderGrpcService> log) : ECommerceGRPC.Orders.OrderService.OrderServiceBase
{
    // Números de pedido en memoria: 1001, 1002, … (se reinician si el contenedor se reinicia).
    private static int _ultimoPedido = 1000;

    public override async Task<ValidateOrderReply> ValidateOrder(ValidateOrderRequest request, ServerCallContext context)
    {
        var (reply, _, _) = await Validar(request, context);
        return reply;
    }

    public override async Task<CreateOrderReply> CreateOrder(ValidateOrderRequest request, ServerCallContext context)
    {
        var (validacion, usuario, lineas) = await Validar(request, context);
        if (!validacion.IsValid)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, string.Join(" ", validacion.Problems)));

        int id = Interlocked.Increment(ref _ultimoPedido);
        var evento = new PedidoCreado(
            EventId: Guid.NewGuid().ToString(),
            OrderId: id,
            UserId: request.UserId,
            Email: usuario!.Email,
            Total: Math.Round((decimal)validacion.Total, 2),
            CreatedAt: DateTime.UtcNow,
            Items: lineas);

        log.LogInformation("Pedido {OrderId} creado: usuario {UserId}, total {Total}", id, request.UserId, evento.Total);
        var pedido = registro.Crear(evento);                     // Clase 4: nace PENDIENTE
        await publicador.PublicarAsync(evento, context.CancellationToken);
        pedido.Paso("Pedido creado: OrderCreated publicado en SNS");

        return new CreateOrderReply { OrderId = id, Total = (double)evento.Total, EventId = evento.EventId };
    }

    // Clase 4 · Estado del pedido en la saga y sus pasos.
    public override Task<OrderStatusReply> GetOrder(GetOrderRequest request, ServerCallContext context)
    {
        var p = registro.Buscar(request.OrderId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"No existe el pedido {request.OrderId}."));
        var reply = new OrderStatusReply
        {
            OrderId = p.OrderId, UserId = p.Evento.UserId, Status = p.Estado, Total = (double)p.Evento.Total,
        };
        reply.Steps.AddRange(p.Pasos);
        return Task.FromResult(reply);
    }

    // Validación compartida por ValidateOrder y CreateOrder.
    private async Task<(ValidateOrderReply, UserReply?, List<LineaPedidoCreado>)> Validar(
        ValidateOrderRequest request, ServerCallContext context)
    {
        // Identidad propagada por el gateway en los metadatos (encabezado x-usuario).
        var quien = context.RequestHeaders.GetValue("x-usuario") ?? "(sin identidad)";
        log.LogInformation("Validación pedida por {Quien}: usuario {UserId}, {N} líneas",
            quien, request.UserId, request.Items.Count);

        if (request.Items.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "El pedido no tiene productos."));

        var reply = new ValidateOrderReply();
        var lineas = new List<LineaPedidoCreado>();
        UserReply? usuario = null;

        // 1.ª llamada: ¿existe el usuario y está activo? (UserService, puerto 7002)
        try
        {
            usuario = await usuarios.GetUserAsync(new GetUserRequest { Id = request.UserId },
                cancellationToken: context.CancellationToken);
            if (!usuario.IsActive)
                reply.Problems.Add($"El usuario {request.UserId} está desactivado.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            reply.Problems.Add($"No existe el usuario {request.UserId}.");
        }

        // 2.ª llamada, una por línea: ¿existe el producto y alcanza el stock? (ProductService, puerto 7001)
        foreach (var item in request.Items)
        {
            try
            {
                var p = await productos.GetProductAsync(new GetProductRequest { Id = item.ProductId },
                    cancellationToken: context.CancellationToken);
                if (item.Quantity <= 0)
                    reply.Problems.Add($"La cantidad de {p.Name} debe ser mayor que 0.");
                else if (p.Stock < item.Quantity)
                    reply.Problems.Add($"{p.Name}: pidió {item.Quantity} y hay {p.Stock}.");
                else
                {
                    reply.Total += p.Price * item.Quantity;
                    lineas.Add(new LineaPedidoCreado(p.Id, p.Name, item.Quantity, Math.Round((decimal)p.Price, 2)));
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                reply.Problems.Add($"No existe el producto {item.ProductId}.");
            }
        }

        reply.IsValid = reply.Problems.Count == 0;
        reply.Total = Math.Round(reply.Total, 2);
        return (reply, usuario, lineas);
    }
}

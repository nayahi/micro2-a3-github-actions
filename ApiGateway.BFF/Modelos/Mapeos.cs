using System.Globalization;
using ECommerceGRPC.Orders;
using ECommerceGRPC.Products;
using ECommerceGRPC.Users;

namespace ApiGateway.BFF.Modelos;

// Transformación de datos en el gateway: mensaje gRPC ⇄ JSON del cliente REST.
public static class Mapeos
{
    // Respuesta: ProductReply (gRPC) → ProductoDto (REST).
    public static ProductoDto ADto(ProductReply p) => new()
    {
        Id = p.Id,
        Nombre = p.Name,
        Descripcion = p.Description,
        Precio = Math.Round((decimal)p.Price, 2),   // proto3 no tiene decimal: double → decimal de dinero
        Categoria = p.Category,
        Stock = p.Stock,
        Disponible = p.IsActive && p.Stock > 0,     // campo calculado: no existe en el contrato gRPC
    };

    // Solicitud: ActualizarStockDto (REST) → UpdateStockRequest (gRPC). El id viene de la ruta.
    public static UpdateStockRequest AUpdateStock(int id, ActualizarStockDto dto)
    {
        return new UpdateStockRequest { Id = id, Stock = dto.Cantidad };
    }

    // UserReply → UsuarioDto: une nombre y apellido, y convierte el texto ISO 8601 en fecha.
    public static UsuarioDto ADto(UserReply u) => new(
        u.Id,
        u.Email,
        $"{u.Name} {u.LastName}".Trim(),
        u.IsActive,
        DateTime.Parse(u.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));

    // PedidoDto + id del token → ValidateOrderRequest. El usuario NO viene en el JSON: sale del JWT.
    public static ValidateOrderRequest AValidateOrder(int userId, PedidoDto dto)
    {
        var req = new ValidateOrderRequest { UserId = userId };
        req.Items.AddRange(dto.Lineas.Select(l => new OrderItem { ProductId = l.ProductoId, Quantity = l.Cantidad }));
        return req;
    }

    public static PedidoCreadoDto ADto(CreateOrderReply r) =>
        new(r.OrderId, Math.Round((decimal)r.Total, 2), r.EventId,
            "PENDIENTE. La saga reserva el stock y cobra; consulte GET /api/v1/pedidos/" + r.OrderId);

    public static EstadoPedidoDto ADto(OrderStatusReply r) =>
        new(r.OrderId, r.Status, Math.Round((decimal)r.Total, 2), r.Steps.ToList());

    public static ValidacionPedidoDto ADto(ValidateOrderReply r) =>
        new(r.IsValid, r.Problems.ToList(), Math.Round((decimal)r.Total, 2));
}

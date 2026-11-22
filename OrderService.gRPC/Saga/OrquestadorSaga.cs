using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using ECommerceGRPC.Payments;
using ECommerceGRPC.Products;
using Grpc.Core;
using OrderService.gRPC.Mensajeria;

namespace OrderService.gRPC.Saga;

// Clase 4 · Saga ORQUESTADA del pedido. OrderService es el orquestador: decide el orden de los pasos y,
// si uno falla, qué deshacer. Lee OrderCreated de la cola «pedidos-procesar» (la que en la Clase 3 nadie leía).
//
//   Paso 1 · Inventario  ReserveStock (ProductService)  ── su compensación: ReleaseStock
//   Paso 2 · Pago        Charge (PaymentService)        ── si se rechaza: compensar el paso 1 → CANCELADO
//   Fin                  CONFIRMADO
//
// Dos clases de fallo, dos respuestas distintas:
//   · De negocio (FAILED_PRECONDITION, «pago rechazado»): reintentar no sirve → se COMPENSA y se cancela.
//   · Transitorio (UNAVAILABLE, DEADLINE_EXCEEDED, «Pagos no responde»): no se compensa. El mensaje NO se borra:
//     SQS lo vuelve a entregar cuando vence el visibility timeout (10 s). La cola es el reintento.
//     Como los pasos son idempotentes, repetirlos no descuenta ni cobra dos veces.
public class OrquestadorSaga(
    IAmazonSQS sqs,
    IConfiguration config,
    RegistroPedidos registro,
    ProductService.ProductServiceClient productos,
    PaymentService.PaymentServiceClient pagos,
    ILogger<OrquestadorSaga> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string url = await EsperarCola(config["Mensajeria:ColaProcesar"]!, ct);
        log.LogInformation("Saga: escuchando la cola {Url}", url);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = url,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,                                      // long polling (Clase 3)
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                }, ct);
                foreach (var m in resp.Messages ?? [])
                    await Procesar(url, m, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError("Saga: error al procesar la cola: {Error}. Reintento en 5 s.", ex.Message);
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task Procesar(string url, Message m, CancellationToken ct)
    {
        int intento = m.Attributes is not null && m.Attributes.TryGetValue("ApproximateReceiveCount", out var c) ? int.Parse(c) : 1;
        var e = JsonSerializer.Deserialize<PedidoCreado>(m.Body);
        if (e is null) { log.LogError("Saga: mensaje ilegible; no se borra."); return; }

        var pedido = registro.Crear(e);
        if (pedido.Terminado)                       // el mensaje llegó otra vez, pero la saga ya terminó
        {
            await Borrar(url, m, ct);
            return;
        }

        // ── Paso 1 · Inventario: reservar el stock ──────────────────────────
        try
        {
            var req = new ReserveStockRequest { OrderId = e.OrderId };
            req.Lines.AddRange(e.Items.Select(i => new StockLine { ProductId = i.ProductId, Quantity = i.Quantity }));
            var r = await productos.ReserveStockAsync(req, cancellationToken: ct);
            pedido.Paso(r.AlreadyReserved ? "Inventario: la reserva ya existía (mensaje repetido)" : "Inventario: stock reservado");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            pedido.Cancelar($"Inventario: {ex.Status.Detail} No hay nada que compensar.");
            log.LogWarning("Saga {Pedido}: CANCELADO en el paso 1 ({Motivo})", e.OrderId, ex.Status.Detail);
            await Borrar(url, m, ct);
            return;
        }
        catch (RpcException ex) when (Transitorio(ex))
        {
            pedido.Paso($"Inventario no responde ({ex.StatusCode}), intento {intento}: el mensaje vuelve a la cola en 10 s");
            return;
        }

        // ── Paso 2 · Pago: cobrar ────────────────────────────────────────────
        try
        {
            var cobro = await pagos.ChargeAsync(new ChargeRequest
            {
                OrderId = e.OrderId, UserId = e.UserId, Amount = (double)e.Total,
            }, cancellationToken: ct);
            pedido.Confirmar($"Pago: cobrado (pago {cobro.PaymentId})");
            log.LogInformation("Saga {Pedido}: CONFIRMADO (pago {Pago})", e.OrderId, cobro.PaymentId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            pedido.Paso($"Pago rechazado: {ex.Status.Detail}");
            await Compensar(pedido, ct);
        }
        catch (RpcException ex) when (Transitorio(ex))
        {
            // Fallo transitorio: no se compensa ni se borra. SQS lo vuelve a entregar a los 10 s.
            pedido.Paso($"Pagos no responde ({ex.StatusCode}), intento {intento}: el mensaje vuelve a la cola en 10 s");
            log.LogWarning("Saga {Pedido}: Pagos no responde ({Codigo}); se reintenta con la cola (intento {Intento}).",
                e.OrderId, ex.StatusCode, intento);
            return;
        }

        await Borrar(url, m, ct);
    }

    // Compensación del paso 1: el pago no se hizo, así que el stock reservado se devuelve y el pedido se cancela.
    private async Task Compensar(PedidoSaga pedido, CancellationToken ct)
    {
        var r = await productos.ReleaseStockAsync(new ReleaseStockRequest { OrderId = pedido.OrderId }, cancellationToken: ct);
        pedido.Cancelar(r.Released ? "Compensación: stock liberado" : "Compensación: no había stock reservado");
        log.LogWarning("Saga {Pedido}: CANCELADO y compensado (stock liberado: {Liberado})", pedido.OrderId, r.Released);
    }

    private static bool Transitorio(RpcException ex) =>
        ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    private async Task Borrar(string url, Message m, CancellationToken ct) =>
        await sqs.DeleteMessageAsync(url, m.ReceiptHandle, ct);

    private async Task<string> EsperarCola(string nombre, CancellationToken ct)
    {
        while (true)
        {
            try { return (await sqs.GetQueueUrlAsync(nombre, ct)).QueueUrl; }
            catch (Exception ex) when (ex is QueueDoesNotExistException or HttpRequestException or Amazon.Runtime.AmazonServiceException)
            {
                log.LogWarning("Saga: la cola {Cola} todavía no existe o LocalStack no responde; reintento en 3 s.", nombre);
                await Task.Delay(3000, ct);
            }
        }
    }
}

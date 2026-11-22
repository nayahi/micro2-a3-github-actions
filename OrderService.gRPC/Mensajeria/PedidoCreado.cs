namespace OrderService.gRPC.Mensajeria;

// El CONTRATO del evento OrderCreated: es el JSON que viaja por SNS y SQS.
// Pedidos lo publica y no sabe quién lo lee. Notificaciones tiene su propia copia
// de esta clase (mismos nombres de propiedad): lo que se comparte es el JSON, no el código.
//   EventId → único por publicación; el consumidor lo usa para no procesar dos veces.
public record PedidoCreado(
    string EventId,
    int OrderId,
    int UserId,
    string Email,
    decimal Total,
    DateTime CreatedAt,
    List<LineaPedidoCreado> Items);

public record LineaPedidoCreado(int ProductId, string ProductName, int Quantity, decimal UnitPrice);

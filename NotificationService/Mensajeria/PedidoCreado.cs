namespace NotificationService.Mensajeria;

// Copia del contrato del evento OrderCreated (el original está en OrderService.gRPC/Mensajeria).
// Lo que se comparte entre los dos servicios es el JSON: los nombres de las propiedades deben coincidir.
public record PedidoCreado(
    string EventId,
    int OrderId,
    int UserId,
    string Email,
    decimal Total,
    DateTime CreatedAt,
    List<LineaPedidoCreado> Items);

public record LineaPedidoCreado(int ProductId, string ProductName, int Quantity, decimal UnitPrice);

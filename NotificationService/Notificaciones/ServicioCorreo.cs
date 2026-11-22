using System.Collections.Concurrent;
using NotificationService.Mensajeria;

namespace NotificationService.Notificaciones;

// «Envía» la notificación del pedido. En el curso no se manda un correo real: se registra en memoria
// y se consulta con GET /notificaciones. La simulación «correo caído» hace que cada envío falle,
// para ver qué pasa con un mensaje que no se puede procesar (reintentos y DLQ).
public class ServicioCorreo(ILogger<ServicioCorreo> log)
{
    private readonly ConcurrentQueue<NotificacionEnviada> _enviadas = new();

    public bool CorreoCaido { get; set; }

    public IReadOnlyCollection<NotificacionEnviada> Enviadas => _enviadas.ToArray();

    public Task EnviarAsync(PedidoCreado e, int intento, CancellationToken ct)
    {
        if (CorreoCaido)
            throw new CorreoNoDisponibleException("el servidor de correo no responde (simulación activa)");

        _enviadas.Enqueue(new NotificacionEnviada(e.OrderId, e.EventId, e.Email, e.Total, intento, DateTime.UtcNow));
        log.LogInformation("Notificación enviada a {Email}: pedido {OrderId} por {Total} (intento {Intento})",
            e.Email, e.OrderId, e.Total, intento);
        return Task.CompletedTask;
    }
}

public record NotificacionEnviada(int PedidoId, string EventoId, string Email, decimal Total, int Intento, DateTime EnviadaEl);

public class CorreoNoDisponibleException(string mensaje) : Exception(mensaje);

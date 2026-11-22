using System.Collections.Concurrent;

namespace NotificationService.Notificaciones;

// Memoria de los eventos ya procesados (clave: EventId).
// SQS entrega «al menos una vez»: el mismo evento puede llegar dos veces. Con esta memoria,
// el segundo se reconoce y no se notifica de nuevo (idempotencia).
// Límite del curso: vive en memoria y se pierde si el servicio se reinicia. En producción va en una
// tabla con el EventId como clave única (o en DynamoDB con una escritura condicional).
public class ControlDuplicados
{
    private readonly ConcurrentDictionary<string, DateTime> _procesados = new();

    public bool YaProcesado(string eventId) => _procesados.ContainsKey(eventId);

    public void Marcar(string eventId) => _procesados.TryAdd(eventId, DateTime.UtcNow);
}

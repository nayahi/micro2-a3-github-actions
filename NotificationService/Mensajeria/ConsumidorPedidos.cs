using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using NotificationService.Notificaciones;

namespace NotificationService.Mensajeria;

// Consumidor de la cola «pedidos-notificar»: un ciclo que corre mientras el servicio esté encendido.
//   recibir (espera larga) → leer el JSON → ¿duplicado? → notificar → BORRAR el mensaje.
// Si algo falla y el mensaje NO se borra, SQS lo vuelve a mostrar cuando vence el visibility timeout
// (5 s en esta cola). Después de 3 recepciones fallidas, la redrive policy lo manda a la DLQ.
public class ConsumidorPedidos(
    IAmazonSQS sqs,
    IConfiguration config,
    ServicioCorreo correo,
    ControlDuplicados duplicados,
    ILogger<ConsumidorPedidos> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string url = await EsperarCola(config["Mensajeria:ColaNotificar"]!, ct);
        log.LogInformation("Escuchando la cola {Url}", url);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var m in await Recibir(url, ct))
                    await Procesar(url, m, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError("No se pudo leer la cola: {Error}. Reintento en 5 s.", ex.Message);
                await Task.Delay(5000, ct);
            }
        }
    }

    // Recibe hasta 10 mensajes. Con WaitTimeSeconds = 20 (long polling) la llamada espera
    // hasta 20 s a que llegue algo, en vez de responder vacía al instante y gastar llamadas.
    private async Task<List<Message>> Recibir(string url, CancellationToken ct)
    {
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 20,                                      // long polling
            MessageSystemAttributeNames = ["ApproximateReceiveCount"], // cuántas veces se ha recibido
        }, ct);
        return resp.Messages ?? [];
    }

    private async Task Procesar(string url, Message m, CancellationToken ct)
    {
        int intento = m.Attributes is not null && m.Attributes.TryGetValue("ApproximateReceiveCount", out var c)
            ? int.Parse(c) : 1;

        PedidoCreado? e;
        try { e = JsonSerializer.Deserialize<PedidoCreado>(m.Body); }
        catch (JsonException) { e = null; }
        if (e is null || string.IsNullOrEmpty(e.EventId))
        {
            log.LogError("Mensaje ilegible (intento {Intento}); no se borra: {Cuerpo}", intento, m.Body);
            return;
        }

        if (duplicados.YaProcesado(e.EventId))
        {
            log.LogWarning("Duplicado: el evento {EventId} (pedido {OrderId}) ya se procesó. Se borra sin notificar.",
                e.EventId, e.OrderId);
            await Borrar(url, m, ct);
            return;
        }

        try
        {
            await correo.EnviarAsync(e, intento, ct);
        }
        catch (CorreoNoDisponibleException ex)
        {
            log.LogError("Pedido {OrderId}: el intento {Intento} falló ({Motivo}). El mensaje NO se borra: vuelve a la cola en 5 s.",
                e.OrderId, intento, ex.Message);
            return;
        }

        duplicados.Marcar(e.EventId);

        await Borrar(url, m, ct);
    }

    // Borrar = confirmar a SQS que el mensaje ya se procesó. Se usa el ReceiptHandle de ESTA recepción.
    private async Task Borrar(string url, Message m, CancellationToken ct)
    {
        await sqs.DeleteMessageAsync(url, m.ReceiptHandle, ct);
        log.LogInformation("Mensaje {MessageId} borrado de la cola.", m.MessageId);
    }

    // LocalStack crea las colas al arrancar: si el servicio llega antes, espera.
    private async Task<string> EsperarCola(string nombre, CancellationToken ct)
    {
        while (true)
        {
            try { return (await sqs.GetQueueUrlAsync(nombre, ct)).QueueUrl; }
            catch (Exception ex) when (ex is QueueDoesNotExistException or HttpRequestException or Amazon.Runtime.AmazonServiceException)
            {
                log.LogWarning("La cola {Cola} todavía no existe o LocalStack no responde; reintento en 3 s.", nombre);
                await Task.Delay(3000, ct);
            }
        }
    }
}

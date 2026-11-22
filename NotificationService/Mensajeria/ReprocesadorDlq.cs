using Amazon.SQS;
using Amazon.SQS.Model;

namespace NotificationService.Mensajeria;

// Reprocesar = sacar los mensajes de la DLQ y devolverlos a la cola original, una vez corregida la causa.
// (En AWS real también existe «redrive» desde la consola o con StartMessageMoveTask; aquí se hace a mano
// para ver los tres pasos: recibir de la DLQ → enviar a la cola → borrar de la DLQ.)
public class ReprocesadorDlq(IAmazonSQS sqs, IConfiguration config, ILogger<ReprocesadorDlq> log)
{
    private string Cola => config["Mensajeria:ColaNotificar"]!;
    private string Dlq => config["Mensajeria:ColaNotificarDlq"]!;

    public async Task<int> ReprocesarAsync(CancellationToken ct)
    {
        string urlCola = (await sqs.GetQueueUrlAsync(Cola, ct)).QueueUrl;
        string urlDlq = (await sqs.GetQueueUrlAsync(Dlq, ct)).QueueUrl;
        int devueltos = 0;

        while (true)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = urlDlq,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
            }, ct);
            if (resp.Messages is null || resp.Messages.Count == 0) break;

            foreach (var m in resp.Messages)
            {
                await sqs.SendMessageAsync(urlCola, m.Body, ct);
                await sqs.DeleteMessageAsync(urlDlq, m.ReceiptHandle, ct);
                devueltos++;
            }
        }
        log.LogWarning("Reprocesamiento: {N} mensaje(s) devueltos de {Dlq} a {Cola}.", devueltos, Dlq, Cola);
        return devueltos;
    }

    public async Task<Dictionary<string, int>> ContarAsync(CancellationToken ct)
    {
        var r = new Dictionary<string, int>();
        foreach (var nombre in new[] { Cola, Dlq })
        {
            try
            {
                string url = (await sqs.GetQueueUrlAsync(nombre, ct)).QueueUrl;
                var a = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = url,
                    AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                }, ct);
                r[nombre + " (visibles)"] = a.ApproximateNumberOfMessages;
                r[nombre + " (en proceso)"] = a.ApproximateNumberOfMessagesNotVisible;
            }
            catch (QueueDoesNotExistException) { r[nombre + " (no existe)"] = 0; }
        }
        return r;
    }
}

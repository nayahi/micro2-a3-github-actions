using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace OrderService.gRPC.Mensajeria;

// Publica OrderCreated en el tópico SNS «pedidos-creados».
// SNS entrega una copia a CADA cola suscrita (fan-out): pedidos-procesar y pedidos-notificar.
// Pedidos no sabe quién lee ni espera respuesta: por eso se dice que está desacoplado.
public class PublicadorPedidos(
    IAmazonSimpleNotificationService sns,
    IConfiguration config,
    ILogger<PublicadorPedidos> log)
{
    public async Task<string> PublicarAsync(PedidoCreado evento, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(evento);

        var resp = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = config["Mensajeria:TopicoPedidos"],   // arn:aws:sns:us-east-1:000000000000:pedidos-creados
            Message = json,
        }, ct);
        log.LogInformation("OrderCreated publicado: pedido {OrderId}, evento {EventId}, MessageId SNS {MessageId}",
            evento.OrderId, evento.EventId, resp.MessageId);
        return resp.MessageId;
    }
}

// NotificationService · puerto 5003 · Clase 3.
//   Por dentro: un consumidor (BackgroundService) que lee la cola SQS «pedidos-notificar».
//   Por fuera: unos endpoints REST de apoyo para la clase (no pasan por el gateway):
//     GET  /notificaciones                      → lo que ya se envió
//     GET  /colas                               → mensajes en pedidos-notificar y en su DLQ
//     POST /simulacion/correo-caido?activo=true → cada envío falla (para ver reintentos y DLQ)
//     POST /dlq/reprocesar                      → devuelve los mensajes de la DLQ a la cola
using Amazon.Runtime;
using Amazon.SQS;
using NotificationService.Mensajeria;
using NotificationService.Notificaciones;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
var config = builder.Configuration;

// Cliente de SQS apuntando a LocalStack. Con AWS real se quita ServiceURL y se usan credenciales reales.
builder.Services.AddSingleton<IAmazonSQS>(_ =>
    new AmazonSQSClient(
        new BasicAWSCredentials("test", "test"),
        new AmazonSQSConfig
        {
            ServiceURL = config["AWS:ServiceURL"],          // http://localstack:4566
            AuthenticationRegion = config["AWS:Region"],     // us-east-1
        }));
builder.Services.AddSingleton<ServicioCorreo>();
builder.Services.AddSingleton<ControlDuplicados>();
builder.Services.AddSingleton<ReprocesadorDlq>();
builder.Services.AddHostedService<ConsumidorPedidos>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { estado = "ok", servicio = "NotificationService" }));

app.MapGet("/notificaciones", (ServicioCorreo correo) => Results.Ok(correo.Enviadas));

app.MapGet("/colas", async (ReprocesadorDlq dlq, CancellationToken ct) => Results.Ok(await dlq.ContarAsync(ct)));

app.MapPost("/simulacion/correo-caido", (bool activo, ServicioCorreo correo, ILogger<Program> log) =>
{
    correo.CorreoCaido = activo;
    log.LogWarning("Simulación: correo {Estado}", activo ? "CAÍDO (cada envío falla)" : "funcionando");
    return Results.Ok(new { correoCaido = activo });
});

app.MapPost("/dlq/reprocesar", async (ReprocesadorDlq dlq, CancellationToken ct) =>
    Results.Ok(new { devueltos = await dlq.ReprocesarAsync(ct) }));

Log.Information("NotificationService escuchando en http://localhost:5003");
app.Run();

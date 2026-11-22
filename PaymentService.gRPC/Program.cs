// PaymentService.gRPC · Clase 4 · servicio de Pagos SIMULADO.
//   Puerto 7004 (HTTP/2): el contrato gRPC (Protos/payment.proto). Lo llama la saga de OrderService.
//   Puerto 5004 (HTTP/1.1): endpoints de apoyo para la clase (no pasan por el gateway):
//     GET  /pagos                               → los pagos cobrados
//     POST /simulacion/pagos?tasaRechazo=0.5    → proporción de pagos que se rechazan (0 a 1)
using PaymentService.gRPC.Services;
using PaymentService.gRPC.Simulacion;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

builder.Services.AddSingleton<SimuladorPagos>();
builder.Services.AddGrpc(options => options.EnableDetailedErrors = true);
builder.Services.AddGrpcReflection();
// Clase 4: servicio de salud estándar de gRPC (grpc.health.v1). El gateway lo consulta en /health/ready.
builder.Services.AddGrpcHealthChecks(o => o.UseHealthChecksCache = false)   // responde en el momento, sin caché
    .AddCheck("proceso", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

app.MapGrpcService<PaymentGrpcService>();
app.MapGrpcReflectionService();
app.MapGrpcHealthChecksService();

app.MapGet("/health", () => Results.Ok(new { estado = "ok", servicio = "PaymentService.gRPC" }));
app.MapGet("/pagos", (SimuladorPagos s) => Results.Ok(s.Pagos));
app.MapPost("/simulacion/pagos", (double tasaRechazo, SimuladorPagos s, ILogger<Program> log) =>
{
    if (tasaRechazo is < 0 or > 1)
        return Results.BadRequest(new { error = "tasaRechazo va de 0 a 1." });
    s.TasaRechazo = tasaRechazo;
    log.LogWarning("Simulación: se rechaza el {Porcentaje:0} % de los pagos", tasaRechazo * 100);
    return Results.Ok(new { tasaRechazo });
});

Log.Information("PaymentService.gRPC escuchando en 7004 (gRPC) y en http://localhost:5004 (apoyo de la clase)");
app.Run();

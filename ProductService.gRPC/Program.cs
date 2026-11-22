// ProductService.gRPC · puerto 7001 (HTTP/2) · base CatalogDB (la misma de CatalogService)
// Clase 4: ReserveStock/ReleaseStock (Inventario en la saga) y el servicio de salud gRPC.
using Microsoft.EntityFrameworkCore;
using ProductService.gRPC.Data;
using ProductService.gRPC.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));

builder.Services.AddSingleton<StockNotifier>();
builder.Services.AddSingleton<Reservas>();

builder.Services.AddGrpc(options => options.EnableDetailedErrors = true);
// Reflection: permite que grpcurl y Postman descubran el contrato sin el .proto.
// En producción se apaga; en el curso queda encendida.
builder.Services.AddGrpcReflection();
// Clase 4: servicio de salud estándar de gRPC (grpc.health.v1). El gateway lo consulta en /health/ready.
builder.Services.AddGrpcHealthChecks(o => o.UseHealthChecksCache = false)   // responde en el momento, sin caché
    .AddCheck("proceso", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

app.MapGrpcService<ProductGrpcService>();
app.MapGrpcReflectionService();
app.MapGrpcHealthChecksService();

Log.Information("ProductService.gRPC escuchando en el puerto 7001 (HTTP/2)");
app.Run();

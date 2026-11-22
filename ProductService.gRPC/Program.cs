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

// Clase 4: la primera consulta a la base (EF Core + conexión a SQL Server) tarda más de 1 s, que es el timeout
// por intento del gateway. Se hace aquí, antes de atender: si no, la llamada de prueba del circuito medio abierto
// se cancela por timeout y el circuito se vuelve a abrir aunque Productos ya haya vuelto.
try
{
    var reloj = System.Diagnostics.Stopwatch.StartNew();
    using var calentar = app.Services.CreateScope();
    int id = 1;                                     // la misma consulta que GetProduct, para que quede compilada
    await calentar.ServiceProvider.GetRequiredService<CatalogDbContext>().Products.AsNoTracking()
        .FirstOrDefaultAsync(p => p.Id == id);
    Log.Information("Conexión a la base lista en {Ms} ms", reloj.ElapsedMilliseconds);
}
catch (Exception ex) { Log.Warning("No se pudo calentar la conexión a la base: {Error}", ex.Message); }

Log.Information("ProductService.gRPC escuchando en el puerto 7001 (HTTP/2)");
app.Run();

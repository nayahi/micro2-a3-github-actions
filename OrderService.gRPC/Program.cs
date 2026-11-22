// OrderService.gRPC · puerto 7003 (HTTP/2) · sin base de datos.
//   Clase 2: valida pedidos preguntándoles a UserService.gRPC (7002) y a ProductService.gRPC (7001).
//   Clase 3: crea pedidos y publica OrderCreated en SNS (LocalStack, puerto 4566).
//   Clase 4: orquesta la saga del pedido (Saga/OrquestadorSaga.cs): lee pedidos-procesar, reserva el stock
//            en Productos y cobra en Pagos (7004); si el pago se rechaza, compensa.
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using ECommerceGRPC.Payments;
using ECommerceGRPC.Products;
using ECommerceGRPC.Users;
using OrderService.gRPC.Mensajeria;
using OrderService.gRPC.Saga;
using OrderService.gRPC.Services;
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

// OrderService es servidor de su contrato y CLIENTE de Usuarios y Productos.
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
    o.Address = new Uri(config["GrpcServicios:Usuarios"]!));
builder.Services.AddGrpcClient<ProductService.ProductServiceClient>(o =>
    o.Address = new Uri(config["GrpcServicios:Productos"]!));
// Clase 4: cliente de Pagos, para el paso 2 de la saga.
builder.Services.AddGrpcClient<PaymentService.PaymentServiceClient>(o =>
    o.Address = new Uri(config["GrpcServicios:Pagos"]!))
    // Como en el gateway: con HttpClientHandler la conexión se abre en cada intento. Con el handler por defecto,
    // el canal gRPC queda en espera de reconexión tras la caída y el pedido fallaría también en la entrega siguiente.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());

// Cliente de SNS apuntando a LocalStack. Con AWS real se quita ServiceURL y se usan credenciales reales.
builder.Services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
    new AmazonSimpleNotificationServiceClient(
        new BasicAWSCredentials("test", "test"),
        new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = config["AWS:ServiceURL"],          // http://localstack:4566
            AuthenticationRegion = config["AWS:Region"],     // us-east-1
        }));
builder.Services.AddSingleton<PublicadorPedidos>();

// Clase 4 · Saga: cliente de SQS (lee pedidos-procesar), registro de pedidos y el orquestador.
builder.Services.AddSingleton<IAmazonSQS>(_ =>
    new AmazonSQSClient(
        new BasicAWSCredentials("test", "test"),
        new AmazonSQSConfig { ServiceURL = config["AWS:ServiceURL"], AuthenticationRegion = config["AWS:Region"] }));
builder.Services.AddSingleton<RegistroPedidos>();
builder.Services.AddHostedService<OrquestadorSaga>();

builder.Services.AddGrpc(options => options.EnableDetailedErrors = true);
builder.Services.AddGrpcReflection();
// Clase 4: servicio de salud estándar de gRPC (grpc.health.v1). El gateway lo consulta en /health/ready.
builder.Services.AddGrpcHealthChecks(o => o.UseHealthChecksCache = false)   // responde en el momento, sin caché
    .AddCheck("proceso", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());

var app = builder.Build();

app.MapGrpcService<OrderGrpcService>();
app.MapGrpcReflectionService();
app.MapGrpcHealthChecksService();

Log.Information("OrderService.gRPC escuchando en el puerto 7003 (HTTP/2) · publica en {Topico}", config["Mensajeria:TopicoPedidos"]);
app.Run();

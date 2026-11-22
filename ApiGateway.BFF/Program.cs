// ApiGateway.BFF · puerto 5000 · REST público afuera, gRPC adentro.
//   Afuera: el navegador, Postman y Swagger hablan JSON por HTTP/1.1.
//   Adentro: el gateway habla gRPC (HTTP/2) con Productos (7001), Usuarios (7002) y Pedidos (7003).
//   Clase 4: cada cliente gRPC lleva Polly (reintento + circuit breaker + timeout) y hay health checks
//            de liveness (/health/live) y readiness (/health/ready).
using System.Text;
using System.Threading.RateLimiting;
using ApiGateway.BFF.Errores;
using ApiGateway.BFF.Metricas;
using ApiGateway.BFF.Resiliencia;
using ApiGateway.BFF.Salud;
using ApiGateway.BFF.Seguridad;
using ECommerceGRPC.Orders;
using ECommerceGRPC.Products;
using ECommerceGRPC.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
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

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
// Traduce los errores gRPC (NotFound, InvalidArgument, Unavailable…) a códigos HTTP (404, 400, 503…).
builder.Services.AddExceptionHandler<RpcExceptionHandler>();

// ── Métricas por protocolo y propagación de identidad ─────────────────────
builder.Services.AddSingleton<MetricasProtocolo>();
builder.Services.AddSingleton<MetricasGrpcInterceptor>();
builder.Services.AddSingleton<PropagarIdentidadInterceptor>();
builder.Services.AddSingleton<EventosResiliencia>();      // Clase 4: intentos, reintentos y estado de cada circuito

// ── Clientes gRPC: el gateway llama a los servicios de adentro ────────────
builder.Services.AddGrpcClient<ProductService.ProductServiceClient>(o =>
        o.Address = new Uri(config["GrpcServicios:Productos"]!))
    .AddInterceptor<PropagarIdentidadInterceptor>()
    .AddInterceptor<MetricasGrpcInterceptor>()
    .ConResiliencia("productos");                       // Clase 4: Resiliencia/ResilienciaGrpc.cs

builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
        o.Address = new Uri(config["GrpcServicios:Usuarios"]!))
    .AddInterceptor<PropagarIdentidadInterceptor>()
    .AddInterceptor<MetricasGrpcInterceptor>()
    .ConResiliencia("usuarios");

builder.Services.AddGrpcClient<OrderService.OrderServiceClient>(o =>
        o.Address = new Uri(config["GrpcServicios:Pedidos"]!))
    .AddInterceptor<PropagarIdentidadInterceptor>()
    .AddInterceptor<MetricasGrpcInterceptor>()
    .ConResiliencia("pedidos");

// ── JWT propio: el gateway emite el token y lo valida ─────────────────────
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;            // deja los nombres cortos: sub, email, role
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config["Jwt:Emisor"],              // ApiGateway.BFF
            ValidateAudience = true,
            ValidAudience = config["Jwt:Audiencia"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,                       // vencido es vencido, sin 5 min de gracia
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Clave"]!)),
            NameClaimType = "email",
            RoleClaimType = "role",
        };
    });
builder.Services.AddAuthorization();

// ── Rate limiting diferenciado por endpoint ───────────────────────────────
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)espera.TotalSeconds).ToString();
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Demasiadas solicitudes. Espere y vuelva a intentar." }, ct);
    };

    // Lecturas públicas: 10 solicitudes cada 10 segundos por dirección IP.
    o.AddPolicy("lecturas", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromSeconds(10) }));

    // Clase 3 · Pedidos: 20 por minuto por usuario.
    o.AddPolicy("pedidos", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? "anonimo",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));

    // Inicio de sesión: 10 intentos por minuto por IP (frena a quien adivina claves).
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

    // Clase 4 · Consultas del estado de un pedido: 60 por minuto por usuario (se consultan seguido).
    o.AddPolicy("consultas", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? "anonimo",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));

    // Escrituras: 3 solicitudes cada 30 segundos por USUARIO (sale del token, no de la IP).
    o.AddPolicy("escrituras", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? "anonimo",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromSeconds(30) }));
});

// ── Swagger UI: la documentación se arma sola con los controladores ───────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ApiGateway.BFF · ECommerceGRPC",
        Version = "v1",
        Description = "REST público que responde desde los servicios gRPC de adentro.",
    });
    c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "ApiGateway.BFF.xml"));
    // Botón «Authorize»: se pega el token y Swagger lo envía en el encabezado Authorization.
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Pegue solo el token (sin la palabra Bearer).",
    });
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", doc)] = [],
    });
});

// ── Clase 4 · Health checks ──────────────────────────────────────────────
//   Liveness  (/health/live):  ¿el proceso del gateway está vivo? No le pregunta a nadie más.
//   Readiness (/health/ready): ¿puede atender? Le pregunta a cada servicio gRPC del que depende.
var salud = builder.Services.AddHealthChecks();
salud.AddCheck("productos", new SaludServicioGrpc(config["GrpcServicios:Productos"]!), tags: ["ready"]);
salud.AddCheck("usuarios", new SaludServicioGrpc(config["GrpcServicios:Usuarios"]!), tags: ["ready"]);
salud.AddCheck("pedidos", new SaludServicioGrpc(config["GrpcServicios:Pedidos"]!), tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<MetricasRestMiddleware>();   // mide cada solicitud REST que entra
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { estado = "ok", servicio = "ApiGateway.BFF" }))
   .ExcludeFromDescription();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = RespuestaSalud.Escribir });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = RespuestaSalud.Escribir });

Log.Information("ApiGateway.BFF escuchando en http://localhost:5000 · Swagger en /swagger");
app.Run();

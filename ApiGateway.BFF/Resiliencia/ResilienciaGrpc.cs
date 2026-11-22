using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ApiGateway.BFF.Resiliencia;

// Clase 4 · Polly en las llamadas gRPC que salen del gateway.
// Un cliente gRPC de .NET usa un HttpClient por dentro, así que la resiliencia se agrega como un handler
// de ese HttpClient (paquete Microsoft.Extensions.Http.Resilience, que es Polly v8). Cada servicio tiene
// su propia tubería y su propio circuito: si Productos se cae, el circuito de Usuarios sigue cerrado.
//
//   solicitud → [Reintento] → [Circuit breaker] → [Timeout por intento] → [ContadorIntentos] → red
//
// Solo se reintenta lo TRANSITORIO: el servicio no responde (HttpRequestException), un intento que pasa de
// 1 s (timeout) o un 5xx. Un NotFound o un InvalidArgument de gRPC viajan como HTTP 200 con el código en
// el trailer: no son transitorios y no se reintentan. Reintentar un error de negocio solo agrega carga.
public static class ResilienciaGrpc
{
    public static IHttpClientBuilder ConResiliencia(this IHttpClientBuilder cliente, string servicio)
    {
        cliente.AddResilienceHandler($"grpc-{servicio}", (tuberia, contexto) =>
        {
            var eventos = contexto.ServiceProvider.GetRequiredService<EventosResiliencia>();
            var log = contexto.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Resiliencia");

            // Reintento: 3 veces, esperando cada vez el doble (≈ 0,2 s, 0,4 s, 0,8 s) con jitter (azar) para que
            // mil clientes no reintenten en el mismo milisegundo.
            tuberia.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
                OnRetry = a =>
                {
                    eventos.Reintento(servicio);
                    log.LogWarning("{Servicio}: reintento {N} de 3 dentro de {Ms} ms ({Motivo})", servicio,
                        a.AttemptNumber + 1, (int)a.RetryDelay.TotalMilliseconds,
                        a.Outcome.Exception?.GetType().Name ?? $"HTTP {(int?)a.Outcome.Result?.StatusCode}");
                    return default;
                },
            });

            // Circuit breaker: si en 30 s hubo al menos 5 intentos y la mitad falló, el circuito se ABRE durante 15 s.
            // Abierto, la llamada ni siquiera sale del gateway: falla al instante con BrokenCircuitException (→ 503).
            // A los 15 s queda MEDIO ABIERTO: deja pasar una llamada de prueba; si sale bien, se CIERRA.
            tuberia.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                OnOpened = a =>
                {
                    eventos.Circuito(servicio, "abierto");
                    log.LogError("{Servicio}: circuito ABIERTO por {S} s", servicio, (int)a.BreakDuration.TotalSeconds);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    eventos.Circuito(servicio, "medio abierto");
                    log.LogWarning("{Servicio}: circuito MEDIO ABIERTO: pasa una llamada de prueba", servicio);
                    return default;
                },
                OnClosed = _ =>
                {
                    eventos.Circuito(servicio, "cerrado");
                    log.LogInformation("{Servicio}: circuito CERRADO", servicio);
                    return default;
                },
            });

            // Timeout de CADA intento: un servicio colgado (o un nombre que el DNS tarda ~4 s en rechazar, como
            // pasa con un contenedor apagado) no retiene la solicitud más de 1 s por intento.
            tuberia.AddTimeout(TimeSpan.FromSeconds(1));
        });

        // Va DESPUÉS de la resiliencia: cuenta cada intento que de verdad sale a la red (reintentos incluidos).
        cliente.AddHttpMessageHandler(sp => new ContadorIntentos(sp.GetRequiredService<EventosResiliencia>(), servicio));

        // Trampa de gRPC + Polly: con el handler por defecto (SocketsHttpHandler), el cliente gRPC abre la conexión
        // por su cuenta («subchannel») ANTES de pasar por el HttpClient. Si el servicio está caído, falla con
        // «Error connecting to subchannel» sin que Polly se entere: ni reintento ni circuito.
        // Con HttpClientHandler, la conexión se abre dentro del HttpClient y Polly sí ve cada fallo.
        cliente.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());
        return cliente;
    }
}

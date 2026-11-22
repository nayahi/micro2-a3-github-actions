using Grpc.Core;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ApiGateway.BFF.Errores;

// Traducción de errores: el servicio gRPC responde con un StatusCode de gRPC
// y el cliente REST espera un código HTTP. Aquí se hace la equivalencia, una sola vez.
public class RpcExceptionHandler(ILogger<RpcExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        if (ex is not RpcException rpc) return false;

        // Clase 4 · Circuito abierto: Polly ni siquiera llamó al servicio. Se responde 503 al instante, con
        // Retry-After, en vez de un 500 confuso: el cliente sabe que es temporal y cuándo volver a intentar.
        if (Buscar<BrokenCircuitException>(rpc) is not null)
        {
            const int segundos = 15;            // BreakDuration del circuit breaker (Resiliencia/ResilienciaGrpc.cs)
            log.LogWarning("Circuito abierto: 503 sin llamar al servicio ({Ruta})", http.Request.Path);
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers.RetryAfter = segundos.ToString();
            await http.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 503,
                Title = "Servicio no disponible: circuito abierto.",
                Detail = $"Un servicio interno falló varias veces seguidas. Vuelva a intentar en {segundos} s.",
                Extensions = { ["circuito"] = "abierto" },
            }, ct);
            return true;
        }

        // Clase 4 · Se agotaron los reintentos y el último intento pasó de 1 s: 504, «no respondió a tiempo».
        if (Buscar<TimeoutRejectedException>(rpc) is not null)
        {
            log.LogWarning("Sin respuesta a tiempo después de reintentar: 504 ({Ruta})", http.Request.Path);
            http.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await http.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 504,
                Title = "Un servicio interno no respondió a tiempo.",
                Detail = "Se reintentó y ningún intento respondió en 1 s.",
                Extensions = { ["codigoGrpc"] = rpc.StatusCode.ToString() },
            }, ct);
            return true;
        }

        var status = rpc.StatusCode switch
        {
            StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
            StatusCode.NotFound => StatusCodes.Status404NotFound,
            StatusCode.AlreadyExists => StatusCodes.Status409Conflict,
            StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
            StatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
            StatusCode.Unauthenticated => StatusCodes.Status401Unauthorized,
            StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
            StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError,
        };
        log.LogWarning("gRPC {Codigo} -> HTTP {Http}: {Detalle}", rpc.StatusCode, status, rpc.Status.Detail);

        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = status == 503 ? "Un servicio interno no responde." : "La solicitud no se pudo completar.",
            Detail = rpc.Status.Detail,
            Extensions = { ["codigoGrpc"] = rpc.StatusCode.ToString() },
        }, ct);
        return true;
    }

    // Polly lanza su excepción dentro del HttpClient; gRPC la envuelve en la RpcException (DebugException).
    private static T? Buscar<T>(RpcException rpc) where T : Exception
    {
        for (Exception? e = rpc.Status.DebugException; e is not null; e = e.InnerException)
            if (e is T t) return t;
        return null;
    }
}

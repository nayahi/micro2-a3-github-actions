using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApiGateway.BFF.Salud;

// Clase 4 · Respuesta JSON de /health/live y /health/ready: el estado general y el de cada servicio.
// Healthy → HTTP 200; Unhealthy → HTTP 503 (así lo lee Docker, Kubernetes o un balanceador).
public static class RespuestaSalud
{
    public static Task Escribir(HttpContext http, HealthReport r)
    {
        http.Response.ContentType = "application/json; charset=utf-8";
        var cuerpo = new
        {
            estado = r.Status.ToString(),
            servicios = r.Entries.ToDictionary(e => e.Key, e => new { estado = e.Value.Status.ToString(), detalle = e.Value.Description }),
        };
        return http.Response.WriteAsync(JsonSerializer.Serialize(cuerpo, new JsonSerializerOptions { WriteIndented = true }));
    }
}

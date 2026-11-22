using Grpc.Health.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApiGateway.BFF.Salud;

// Clase 4 · Readiness: ¿están listos los servicios de los que depende el gateway?
// Le pregunta a cada servicio por el protocolo estándar de salud de gRPC (grpc.health.v1.Health/Check).
// No pasa por Polly ni por el circuito: la salud se mide tal cual, sin reintentos que la disimulen.
public class SaludServicioGrpc(string direccion) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var canal = GrpcChannel.ForAddress(direccion);
            var r = await new Health.HealthClient(canal).CheckAsync(new HealthCheckRequest(),
                deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: ct);
            return r.Status == HealthCheckResponse.Types.ServingStatus.Serving
                ? HealthCheckResult.Healthy(direccion)
                : HealthCheckResult.Unhealthy($"{direccion} responde {r.Status}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"{direccion} no responde ({ex.GetType().Name})");
        }
    }
}

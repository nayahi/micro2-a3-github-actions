using System.Collections.Concurrent;

namespace ApiGateway.BFF.Resiliencia;

// Clase 4 · Lo que hizo la resiliencia, por servicio: intentos que salieron a la red, reintentos y estado
// del circuito. Se consulta en GET /metricas/resiliencia (y lo usa Probar-Resiliencia.ps1).
public class EventosResiliencia
{
    private readonly ConcurrentDictionary<string, Estado> _servicios = new();

    public void Intento(string servicio) => Interlocked.Increment(ref Obtener(servicio).Intentos);

    public void Reintento(string servicio) => Interlocked.Increment(ref Obtener(servicio).Reintentos);

    public void Circuito(string servicio, string estado)
    {
        var e = Obtener(servicio);
        e.Circuito = estado;
        if (estado == "abierto") Interlocked.Increment(ref e.Aperturas);
    }

    public object Resumen() => _servicios.OrderBy(k => k.Key).Select(k => new
    {
        servicio = k.Key,
        intentos = k.Value.Intentos,
        reintentos = k.Value.Reintentos,
        circuito = k.Value.Circuito,
        aperturas = k.Value.Aperturas,
    }).ToList();

    private Estado Obtener(string servicio) => _servicios.GetOrAdd(servicio, _ => new Estado());

    private class Estado
    {
        public long Intentos, Reintentos, Aperturas;
        public string Circuito = "cerrado";
    }
}

// Handler interno: cuenta cada intento real hacia el servicio (si el circuito está abierto, no llega aquí).
public class ContadorIntentos(EventosResiliencia eventos, string servicio) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        eventos.Intento(servicio);
        return base.SendAsync(request, ct);
    }
}

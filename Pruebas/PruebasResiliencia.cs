using System.Net;
using System.Text.Json;
using ApiGateway.BFF.Resiliencia;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace ECommerceGRPC.Pruebas;

// Prueban la tubería de Polly del gateway (Clase 4) sin Docker: un handler falso responde 503,
// como un servicio caído. No necesitan ningún secreto.
public class PruebasResiliencia
{
    private sealed class Responde503 : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private static (HttpClient Cliente, EventosResiliencia Eventos) Armar()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<EventosResiliencia>();
        servicios.AddHttpClient("prueba", c => c.BaseAddress = new Uri("http://servicio-caido.local"))
                 .ConResiliencia("prueba")
                 .AddHttpMessageHandler(() => new Responde503());
        var sp = servicios.BuildServiceProvider();
        return (sp.GetRequiredService<IHttpClientFactory>().CreateClient("prueba"),
                sp.GetRequiredService<EventosResiliencia>());
    }

    // Lo mismo que devuelve GET /metricas/resiliencia.
    private static long Intentos(EventosResiliencia e) =>
        JsonDocument.Parse(JsonSerializer.Serialize(e.Resumen())).RootElement[0].GetProperty("intentos").GetInt64();

    [Fact]
    public async Task Un_servicio_caido_recibe_un_intento_y_tres_reintentos()
    {
        var (cliente, eventos) = Armar();

        var r = await cliente.GetAsync("/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(4, Intentos(eventos));
    }

    [Fact]
    public async Task Despues_de_varios_fallos_el_circuito_se_abre_y_falla_sin_llamar()
    {
        var (cliente, eventos) = Armar();

        await cliente.GetAsync("/");                                   // 4 intentos fallidos
        await Assert.ThrowsAnyAsync<BrokenCircuitException>(async () =>
        {
            for (var i = 0; i < 3; i++) await cliente.GetAsync("/");   // el quinto fallo abre el circuito
        });
        var antes = Intentos(eventos);

        await Assert.ThrowsAnyAsync<BrokenCircuitException>(() => cliente.GetAsync("/"));
        Assert.Equal(antes, Intentos(eventos));                        // abierto: no salió a la red
    }
}

using System.Collections.Concurrent;

namespace PaymentService.gRPC.Simulacion;

// Pagos en memoria (se pierden si el contenedor se reinicia) y la simulación de rechazos.
//   TasaRechazo = 0   → todo pago sale bien.
//   TasaRechazo = 1   → todo pago se rechaza.
//   TasaRechazo = 0.5 → la mitad, al azar: «servicio simulado que falla aleatoriamente».
public class SimuladorPagos(IConfiguration config)
{
    private readonly ConcurrentDictionary<int, Pago> _porPedido = new();
    private int _ultimoPago = 5000;

    public double TasaRechazo { get; set; } = config.GetValue<double>("Simulacion:TasaRechazo");

    public IEnumerable<Pago> Pagos => _porPedido.Values.OrderBy(p => p.PagoId);

    public Pago? Buscar(int pedidoId) => _porPedido.GetValueOrDefault(pedidoId);

    public bool Rechazar() => Random.Shared.NextDouble() < TasaRechazo;

    public Pago Registrar(int pedidoId, int usuarioId, decimal monto) =>
        _porPedido.GetOrAdd(pedidoId, _ => new Pago(Interlocked.Increment(ref _ultimoPago), pedidoId, usuarioId, monto, DateTime.UtcNow));
}

public record Pago(int PagoId, int PedidoId, int UsuarioId, decimal Monto, DateTime CobradoEl);

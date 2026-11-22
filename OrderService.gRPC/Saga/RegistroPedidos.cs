using System.Collections.Concurrent;
using OrderService.gRPC.Mensajeria;

namespace OrderService.gRPC.Saga;

// Clase 4 · Estado de cada pedido en la saga, en memoria (se pierde si el contenedor se reinicia).
// En producción iría en la base de Pedidos: es el «diario» que permite saber en qué paso quedó cada saga.
public class RegistroPedidos
{
    private readonly ConcurrentDictionary<int, PedidoSaga> _pedidos = new();

    public PedidoSaga Crear(PedidoCreado e) => _pedidos.GetOrAdd(e.OrderId, _ => new PedidoSaga(e));

    public PedidoSaga? Buscar(int pedidoId) => _pedidos.GetValueOrDefault(pedidoId);
}

public class PedidoSaga(PedidoCreado e)
{
    public const string Pendiente = "PENDIENTE", Confirmado = "CONFIRMADO", Cancelado = "CANCELADO";

    private readonly Lock _candado = new();
    private readonly List<string> _pasos = [];

    public PedidoCreado Evento { get; } = e;
    public int OrderId => Evento.OrderId;
    public string Estado { get; private set; } = Pendiente;
    public bool Terminado => Estado != Pendiente;

    public IReadOnlyList<string> Pasos { get { lock (_candado) return _pasos.ToList(); } }

    public void Paso(string texto)
    {
        lock (_candado) _pasos.Add($"{DateTime.Now:HH:mm:ss} · {texto}");
    }

    public void Confirmar(string texto) { Paso(texto); Estado = Confirmado; }

    public void Cancelar(string texto) { Paso(texto); Estado = Cancelado; }
}

using System.Collections.Concurrent;
using ECommerceGRPC.Products;

namespace ProductService.gRPC.Services;

// Clase 4 · Reservas de stock por pedido (paso 1 de la saga).
// Guardar QUÉ reservó cada pedido es lo que permite compensar (devolver exactamente eso) y ser idempotente
// (no descontar dos veces si el mismo mensaje llega otra vez). En memoria para la clase: en producción iría
// en una tabla de la misma base, dentro de la misma transacción que el cambio de stock.
public class Reservas
{
    private readonly ConcurrentDictionary<int, List<StockLine>> _porPedido = new();

    public bool Existe(int pedidoId) => _porPedido.ContainsKey(pedidoId);

    public void Guardar(int pedidoId, IEnumerable<StockLine> lineas) => _porPedido[pedidoId] = lineas.ToList();

    public List<StockLine>? Quitar(int pedidoId) => _porPedido.TryRemove(pedidoId, out var l) ? l : null;
}

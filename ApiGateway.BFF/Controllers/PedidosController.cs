using ApiGateway.BFF.Modelos;
using ECommerceGRPC.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ApiGateway.BFF.Controllers;

// Tercer servicio gRPC: OrderService.gRPC (7003), que a su vez llama a Usuarios y a Productos.
[ApiController]
[Route("api/v1/pedidos")]
[Authorize]
public class PedidosController(OrderService.OrderServiceClient pedidos) : ControllerBase
{
    /// <summary>Valida un pedido del usuario que inició sesión (no lo guarda).</summary>
    /// <remarks>El id del usuario no va en el JSON: el gateway lo toma del token (claim "sub").</remarks>
    [HttpPost("validar")]
    [EnableRateLimiting("lecturas")]
    [ProducesResponseType<ValidacionPedidoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ValidacionPedidoDto>> Validar(PedidoDto dto, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirst("sub")?.Value, out var userId))
            return Unauthorized(new { error = "Inicie sesión: el pedido sale del usuario del token." });

        var reply = await pedidos.ValidateOrderAsync(Mapeos.AValidateOrder(userId, dto), cancellationToken: ct);
        return Ok(Mapeos.ADto(reply));
    }

    /// <summary>Crea un pedido del usuario que inició sesión (Clases 3 y 4).</summary>
    /// <remarks>Pedidos valida, asigna el número y publica OrderCreated en SNS. Responde 202 sin esperar:
    /// el pedido queda PENDIENTE mientras la saga reserva el stock y cobra. Consulte su estado con
    /// GET /api/v1/pedidos/{id}. Si el pedido no es válido responde 409 con los problemas y no publica nada.</remarks>
    [HttpPost]
    [EnableRateLimiting("pedidos")]
    [ProducesResponseType<PedidoCreadoDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PedidoCreadoDto>> Crear(PedidoDto dto, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirst("sub")?.Value, out var userId))
            return Unauthorized(new { error = "Inicie sesión: el pedido sale del usuario del token." });

        var reply = await pedidos.CreateOrderAsync(Mapeos.AValidateOrder(userId, dto), cancellationToken: ct);
        return Accepted(Mapeos.ADto(reply));
    }

    /// <summary>Estado de un pedido en la saga (Clase 4): PENDIENTE, CONFIRMADO o CANCELADO, con sus pasos.</summary>
    /// <remarks>Solo lo ve su dueño (o el rol Admin). Para cualquier otro usuario responde 404.</remarks>
    [HttpGet("{id:int}")]
    [EnableRateLimiting("consultas")]
    [ProducesResponseType<EstadoPedidoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EstadoPedidoDto>> Estado(int id, CancellationToken ct)
    {
        var reply = await pedidos.GetOrderAsync(new GetOrderRequest { OrderId = id }, cancellationToken: ct);
        if (reply.UserId.ToString() != User.FindFirst("sub")?.Value && !User.IsInRole("Admin"))
            return NotFound(new { error = $"No existe el pedido {id}." });
        return Ok(Mapeos.ADto(reply));
    }
}

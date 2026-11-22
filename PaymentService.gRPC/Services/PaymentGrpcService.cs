using ECommerceGRPC.Payments;
using Grpc.Core;
using PaymentService.gRPC.Simulacion;

namespace PaymentService.gRPC.Services;

// Paso 2 de la saga del pedido: cobrar. Es el paso que puede fallar «por negocio» (pago rechazado).
public class PaymentGrpcService(SimuladorPagos simulador, ILogger<PaymentGrpcService> log)
    : ECommerceGRPC.Payments.PaymentService.PaymentServiceBase
{
    public override Task<ChargeReply> Charge(ChargeRequest request, ServerCallContext context)
    {
        if (request.OrderId <= 0 || request.Amount <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id y amount deben ser mayores que 0."));

        // Idempotencia: el mismo pedido nunca se cobra dos veces, aunque el mensaje llegue otra vez.
        if (simulador.Buscar(request.OrderId) is { } previo)
        {
            log.LogInformation("Pedido {Pedido}: ya estaba cobrado (pago {Pago}); no se cobra otra vez.", request.OrderId, previo.PagoId);
            return Task.FromResult(new ChargeReply { PaymentId = previo.PagoId, AlreadyCharged = true });
        }

        if (simulador.Rechazar())
        {
            log.LogWarning("Pedido {Pedido}: pago RECHAZADO por {Monto:0.00} (simulación).", request.OrderId, request.Amount);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Pago rechazado: fondos insuficientes (simulado)."));
        }

        var pago = simulador.Registrar(request.OrderId, request.UserId, Math.Round((decimal)request.Amount, 2));
        log.LogInformation("Pedido {Pedido}: cobrado {Monto:0.00} (pago {Pago}).", request.OrderId, pago.Monto, pago.PagoId);
        return Task.FromResult(new ChargeReply { PaymentId = pago.PagoId });
    }
}

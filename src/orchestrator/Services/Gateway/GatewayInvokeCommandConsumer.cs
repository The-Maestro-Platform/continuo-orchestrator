using MassTransit;
using Continuo.Shared.Contracts;

namespace Orchestrator.Services.Gateway;

public sealed class GatewayInvokeCommandConsumer : IConsumer<GatewayInvokeCommand> {
    private readonly GatewayInvocationService _invocationService;
    private readonly ILogger<GatewayInvokeCommandConsumer> _logger;

    public GatewayInvokeCommandConsumer(GatewayInvocationService invocationService, ILogger<GatewayInvokeCommandConsumer> logger) {
        _invocationService = invocationService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GatewayInvokeCommand> context) {
        var cmd = context.Message;
        if (_logger.IsEnabled(LogLevel.Information)) {
            _logger.LogInformation("GatewayInvoke received (correlation={CorrelationId}, op={OperationId})", cmd.CorrelationId, cmd.OperationId);
        }

        var result = await _invocationService.InvokeWithRollbackAsync(cmd, context.CancellationToken);

        // Best-effort callback (do not fail the whole message if callback fails).
        if (!string.IsNullOrWhiteSpace(cmd.CallbackOperationId)) {
            try {
                await _invocationService.InvokeCallbackAsync(cmd, result, context.CancellationToken);
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Callback invoke failed (correlation={CorrelationId}, callbackOp={CallbackOperationId})", cmd.CorrelationId, cmd.CallbackOperationId);
            }
        }

        await context.Publish(result, context.CancellationToken);
    }
}


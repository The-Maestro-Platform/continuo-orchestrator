using System.Text;
using Microsoft.EntityFrameworkCore;
using Continuo.Configuration.Extensions;
using Continuo.Shared.Contracts;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services.Gateway;

public sealed class GatewayInvocationService {
    private static readonly string[] TenantHeaderKeys = { "X-Tenant-Slug", "X-Tenant-Code" };

    private readonly OrchestratorDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GatewayInvocationService> _logger;

    public GatewayInvocationService(
        OrchestratorDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GatewayInvocationService> logger) {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GatewayInvokeResult> InvokeAsync(GatewayInvokeCommand cmd, CancellationToken ct) {
        var mode = ServiceUrlResolver.GetMode(_configuration);

        var endpoint = await FindEndpointByOperationIdAsync(cmd.OperationId, ct);
        if (endpoint == null) {
            return new GatewayInvokeResult(
                cmd.CorrelationId,
                Success: false,
                StatusCode: 404,
                ResponseJson: null,
                ErrorCode: "EndpointNotFound",
                ErrorMessage: $"OperationId '{cmd.OperationId}' not registered.",
                RollbackAttempted: false,
                RollbackSuccess: null);
        }

        var serviceUrl = endpoint.Service == null ? null : ServiceUrlSelector.Resolve(endpoint.Service, mode);
        if (string.IsNullOrWhiteSpace(serviceUrl)) {
            return new GatewayInvokeResult(
                cmd.CorrelationId,
                Success: false,
                StatusCode: 503,
                ResponseJson: null,
                ErrorCode: "ServiceUrlMissing",
                ErrorMessage: $"Service base URL not resolved for '{endpoint.Service?.Name ?? "(unknown)"}'.",
                RollbackAttempted: false,
                RollbackSuccess: null);
        }

        return await InvokeEndpointAsync(cmd, serviceUrl, endpoint.Path, endpoint.Method, ct);
    }

    public async Task<GatewayInvokeResult> InvokeWithRollbackAsync(GatewayInvokeCommand cmd, CancellationToken ct) {
        var result = await InvokeAsync(cmd, ct);
        if (result.Success || string.IsNullOrWhiteSpace(cmd.RollbackOperationId)) {
            return result;
        }

        var rollbackAttempted = true;
        bool? rollbackSuccess = null;
        try {
            var rollbackCmd = cmd with {
                OperationId = cmd.RollbackOperationId!,
                BodyJson = cmd.RollbackBodyJson,
                ContentType = cmd.RollbackBodyJson == null ? null : "application/json",
                RollbackOperationId = null,
                RollbackBodyJson = null
            };

            var rollbackResult = await InvokeAsync(rollbackCmd, ct);
            rollbackSuccess = rollbackResult.Success;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Rollback failed for correlation {CorrelationId} (rollbackOp={RollbackOperationId})", cmd.CorrelationId, cmd.RollbackOperationId);
            rollbackSuccess = false;
        }

        return result with {
            RollbackAttempted = rollbackAttempted,
            RollbackSuccess = rollbackSuccess
        };
    }

    public async Task<bool> InvokeCallbackAsync(GatewayInvokeCommand cmd, GatewayInvokeResult result, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(cmd.CallbackOperationId)) {
            return false;
        }

        var callbackCmd = new GatewayInvokeCommand(
            CorrelationId: cmd.CorrelationId,
            OperationId: cmd.CallbackOperationId,
            HttpMethod: "POST",
            Path: "/",
            BodyJson: System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            ContentType: "application/json",
            TenantCode: cmd.TenantCode,
            ClientApp: cmd.ClientApp,
            CallbackOperationId: null,
            RollbackOperationId: null,
            RollbackBodyJson: null);

        var callbackResult = await InvokeAsync(callbackCmd, ct);
        return callbackResult.Success;
    }

    private async Task<EndpointEntry?> FindEndpointByOperationIdAsync(string operationId, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(operationId)) {
            return null;
        }

        return await _db.Endpoints
            .Include(e => e.Service)
            .Where(e => e.Enabled && e.OperationId != null)
            .FirstOrDefaultAsync(e => e.OperationId == operationId, ct);
    }

    private async Task<GatewayInvokeResult> InvokeEndpointAsync(
        GatewayInvokeCommand cmd,
        string serviceBaseUrl,
        string endpointPath,
        string endpointMethod,
        CancellationToken ct) {
        var client = _httpClientFactory.CreateClient("proxy");
        var target = new Uri(serviceBaseUrl.TrimEnd('/') + endpointPath, UriKind.Absolute);

        using var message = new HttpRequestMessage(new HttpMethod(endpointMethod), target);
        if (!string.IsNullOrWhiteSpace(cmd.ClientApp)) {
            message.Headers.TryAddWithoutValidation("X-Client-App", cmd.ClientApp);
        }

        if (!string.IsNullOrWhiteSpace(cmd.TenantCode)) {
            foreach (var key in TenantHeaderKeys) {
                message.Headers.Remove(key);
                message.Headers.TryAddWithoutValidation(key, cmd.TenantCode);
            }
        }

        if (!string.IsNullOrWhiteSpace(cmd.BodyJson)) {
            message.Content = new StringContent(cmd.BodyJson, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(cmd.ContentType)) {
                message.Content.Headers.TryAddWithoutValidation("Content-Type", cmd.ContentType);
            }
        }

        try {
            using var response = await client.SendAsync(message, ct);
            var responseBody = response.Content == null ? null : await response.Content.ReadAsStringAsync(ct);

            return new GatewayInvokeResult(
                cmd.CorrelationId,
                Success: response.IsSuccessStatusCode,
                StatusCode: (int)response.StatusCode,
                ResponseJson: responseBody,
                ErrorCode: response.IsSuccessStatusCode ? null : "HttpError",
                ErrorMessage: response.IsSuccessStatusCode ? null : responseBody,
                RollbackAttempted: false,
                RollbackSuccess: null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) {
            _logger.LogWarning(ex, "Gateway invoke timeout (op={OperationId}, target={Target})", cmd.OperationId, target);
            return new GatewayInvokeResult(
                cmd.CorrelationId,
                Success: false,
                StatusCode: 504,
                ResponseJson: null,
                ErrorCode: "Timeout",
                ErrorMessage: ex.Message,
                RollbackAttempted: false,
                RollbackSuccess: null);
        }
        catch (HttpRequestException ex) {
            _logger.LogWarning(ex, "Gateway invoke failed (op={OperationId}, target={Target})", cmd.OperationId, target);
            return new GatewayInvokeResult(
                cmd.CorrelationId,
                Success: false,
                StatusCode: 503,
                ResponseJson: null,
                ErrorCode: "UpstreamUnavailable",
                ErrorMessage: ex.Message,
                RollbackAttempted: false,
                RollbackSuccess: null);
        }
    }
}


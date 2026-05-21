using System.Text.Json;
using System.Text.Json.Serialization;
using Continuo.Messaging;

namespace Orchestrator.Services.Tenants;

public class TenantDirectoryClient {
    private const string ServiceName = "tenant-api";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() }
    };
    private static int _resolveEndpointAvailability = 0; // 0=unknown, 1=available, -1=missing
    private readonly ServiceCallExecutor _executor;
    private readonly ILogger<TenantDirectoryClient> _logger;
    private readonly Guid? _sagaId;

    public TenantDirectoryClient(
        ServiceCallExecutor executor,
        IConfiguration configuration,
        ILogger<TenantDirectoryClient> logger) {
        _executor = executor;
        _logger = logger;
        var sagaIdValue = configuration["TENANT_SAGA_ID"];
        _sagaId = Guid.TryParse(sagaIdValue, out var parsed) ? parsed : null;
    }

    public async Task<TenantInfo?> GetBySlugAsync(string slug, CancellationToken ct) {
        var response = await SendAsync(HttpMethod.Get, $"/api/tenants/by-slug/{slug}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<TenantDto>(JsonOptions, cancellationToken: ct);
        if (dto == null) {
            return null;
        }

        return new TenantInfo(dto.Id, dto.Slug, dto.Status);
    }

    public async Task<TenantInfo?> GetByIdAsync(Guid id, CancellationToken ct) {
        var response = await SendAsync(HttpMethod.Get, $"/api/tenants/{id}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<TenantDto>(JsonOptions, cancellationToken: ct);
        if (dto == null) {
            return null;
        }

        return new TenantInfo(dto.Id, dto.Slug, dto.Status);
    }

    public async Task<TenantInfo?> ResolveByRequestAsync(string? host, string? clientApp, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(clientApp)) {
            return null;
        }

        await EnsureResolveEndpointAvailabilityAsync(ct);
        if (System.Threading.Volatile.Read(ref _resolveEndpointAvailability) < 0) {
            return null;
        }

        var query = "/api/tenants/resolve?";
        if (!string.IsNullOrWhiteSpace(host)) {
            query += $"host={Uri.EscapeDataString(host)}";
        }
        if (!string.IsNullOrWhiteSpace(clientApp)) {
            query += (query.EndsWith("?") ? string.Empty : "&") + $"clientApp={Uri.EscapeDataString(clientApp)}";
        }

        var response = await SendAsync(HttpMethod.Get, query, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            // Endpoint exists but no match for this host/clientApp pair.
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<TenantDto>(JsonOptions, cancellationToken: ct);
        if (dto == null) {
            return null;
        }

        return new TenantInfo(dto.Id, dto.Slug, dto.Status);
    }

    private async Task EnsureResolveEndpointAvailabilityAsync(CancellationToken ct) {
        if (System.Threading.Volatile.Read(ref _resolveEndpointAvailability) != 0) {
            return;
        }

        try {
            // Probe without query: endpoint exists -> 400 (host/clientApp required), missing endpoint -> 404.
            var probe = await SendAsync(HttpMethod.Get, "/api/tenants/resolve", ct);
            var statusCode = (int)probe.StatusCode;
            if (statusCode == 404) {
                System.Threading.Interlocked.CompareExchange(ref _resolveEndpointAvailability, -1, 0);
                _logger.LogWarning("Tenant resolve endpoint is not available on tenant-api; resolve-by-request will be skipped.");
                return;
            }

            if (statusCode == 400 || (statusCode >= 200 && statusCode < 500)) {
                System.Threading.Interlocked.CompareExchange(ref _resolveEndpointAvailability, 1, 0);
                return;
            }

            // Keep unknown for transient 5xx so we can retry later.
            _logger.LogWarning("Tenant resolve endpoint probe returned unexpected status {StatusCode}.", statusCode);
        }
        catch (Exception ex) {
            // Keep unknown for transient errors; next request can retry probe.
            _logger.LogWarning(ex, "Tenant resolve endpoint probe failed.");
        }
    }

    public async Task<TenantConfigDto?> GetConfigAsync(string slug, CancellationToken ct) {
        var response = await SendAsync(HttpMethod.Get, $"/api/tenants/{slug}/config", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TenantConfigDto>(JsonOptions, cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, CancellationToken ct) {
        try {
            var request = new ApiCallRequest(ServiceName, relativePath, method);
            var result = await _executor.ExecuteAsync(request, _sagaId, compensation: null, cancellationToken: ct);
            return result.Response;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to call {Service} {Method} {Path}", ServiceName, method, relativePath);
            throw;
        }
    }

}

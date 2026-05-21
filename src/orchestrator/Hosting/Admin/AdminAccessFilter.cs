using Microsoft.Extensions.Caching.Memory;
using Continuo.Configuration.Extensions;

namespace Orchestrator.Hosting.Admin;

public sealed class AdminAccessFilter : IEndpointFilter {
    private const string DefaultHeaderName = "X-Admin-Token";
    private const string SecretName = "ORCH__ADMIN__TOKEN";
    private static readonly TimeSpan DefaultReloadCooldown = TimeSpan.FromSeconds(5);

    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _memoryCache;
    private readonly IPlatformSecretResolver _secretResolver;
    private readonly ILogger<AdminAccessFilter> _logger;

    public AdminAccessFilter(
        IConfiguration configuration,
        IMemoryCache memoryCache,
        IPlatformSecretResolver secretResolver,
        ILogger<AdminAccessFilter> logger) {
        _configuration = configuration;
        _memoryCache = memoryCache;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) {
        var http = context.HttpContext;

        // 1) Env-based fast path: ORCH__ADMIN__TOKEN config'te varsa onu kullan.
        //    Staging .env.staging'de set ediliyor → instant resolve.
        var configuredToken =
            _configuration["ORCH:ADMIN:TOKEN"]
            ?? _configuration["ORCH__ADMIN__TOKEN"]
            ?? _configuration["ORCH_ADMIN_TOKEN"];

        // 2) Env yoksa security-api fallback (2026-05-18): dev'de .env.dev'de
        //    propagate edilmedigi icin PlatformSecretResolver uzerinden cek.
        //    Resolver 5dk cache yapar → hot-path overhead minimum. HTTP fail
        //    durumunda exception swallow + null treat (filter 503 doner).
        if (string.IsNullOrWhiteSpace(configuredToken)) {
            try {
                configuredToken = await _secretResolver.TryResolveAsync(SecretName, http.RequestAborted);
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "AdminAccessFilter: PlatformSecretResolver {Name} resolve failed", SecretName);
                configuredToken = null;
            }
        }

        // If a token is configured (env or DB), require it.
        if (!string.IsNullOrWhiteSpace(configuredToken)) {
            var provided = http.Request.Headers[DefaultHeaderName].ToString();
            if (!string.Equals(provided, configuredToken, StringComparison.Ordinal)) {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    detail: $"Missing/invalid {DefaultHeaderName}.");
            }

            // Simple throttle for reload to prevent abuse.
            if (http.Request.Path.StartsWithSegments("/admin/registry/reload", StringComparison.OrdinalIgnoreCase)) {
                var key = "admin:registry:reload:last";
                var now = DateTimeOffset.UtcNow;
                if (_memoryCache.TryGetValue<DateTimeOffset>(key, out var last) && (now - last) < DefaultReloadCooldown) {
                    return Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too Many Requests",
                        detail: $"Try again later (cooldown {DefaultReloadCooldown.TotalSeconds:0}s).");
                }

                _memoryCache.Set(key, now, DefaultReloadCooldown);
            }

            return await next(context);
        }

        // No token configured: allow only authenticated DevOps/admin callers (if auth is enabled),
        // otherwise fail closed to avoid exposing admin operations publicly.
        if (http.User?.Identity?.IsAuthenticated == true) {
            return await next(context);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Admin token not configured",
            detail: "Set ORCH__ADMIN__TOKEN (recommended) or enable auth to use /admin endpoints.");
    }
}


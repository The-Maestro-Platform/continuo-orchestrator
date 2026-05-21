using Microsoft.Extensions.Hosting;
using Continuo.Shared.Context;

namespace Orchestrator.Hosting.Endpoints;

/// <summary>
/// Dev-only session minting endpoint. SADECE Development env'de mapped.
/// UI toolbar bu endpoint'i çağırarak HMAC-signed token alır → outbound HTTP'ye
/// X-Dev-Routing-Token header'ı koyar → consumer'lar marker'ı trust eder.
///
/// Faz 2 mekanizması: shared secret (laptop'ta + dev-api'de aynı env değeri).
/// Faz 3'te mTLS client cert ile değiştirilecek; cert thumbprint'i security-api
/// tablosuyla eşleştirilecek.
///
/// Session persistence: login response'unda JSON token döner (header için) +
/// aynı token HttpOnly cookie olarak set edilir. Reload sonrası toolbar
/// `GET /dev/sessions/me` ile cookie'den re-hydrate eder. JS cookie'yi okuyamaz
/// → XSS payload exfiltrate edemez (token'i alabilmek için /me'ye istek atması
/// lazım, ki o da credentials: 'include' gerektirir).
/// </summary>
public sealed class DevSessionsEndpoints : TechEndpointBase {
    /// <summary>HttpOnly cookie adı — reload sonrası /me ile session re-hydrate edilir.</summary>
    public const string SessionCookieName = "continuo_dev_session";

    public DevSessionsEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        var env = App.Services.GetRequiredService<IHostEnvironment>();
        if (!env.IsDevelopment()) {
            // Production guard — endpoint hiç register edilmez. Header check ALONE
            // da yeter, ama belt-and-suspenders.
            return;
        }

        var validator = App.Services.GetService<IDevRoutingTokenValidator>();
        var options = App.Services.GetService<DevRoutingOptions>();
        if (validator is null || options is null) {
            // AddDevRouting() çağrılmamış — endpoint disable edip log yaz.
            App.Logger.LogWarning(
                "DevSessionsEndpoints: IDevRoutingTokenValidator not registered. AddDevRouting() çağrılmamış. Endpoint pas geçildi.");
            return;
        }

        App.MapPost("/dev/sessions", (HttpContext ctx, DevSessionRequest req) => {
            if (string.IsNullOrWhiteSpace(req.DeveloperId)) {
                return Results.BadRequest(new { error = "developerId required" });
            }
            if (string.IsNullOrWhiteSpace(req.SharedSecret) || req.SharedSecret != options.SharedSecret) {
                // Constant-time string compare bu seviyede önemli değil — endpoint
                // zaten dev-only ve rate-limit'siz, attacker brute-force'a kalkıştığında
                // yine başaramaz çünkü shared secret 32+ karakter random.
                return Results.Unauthorized();
            }

            var lifetime = options.TokenLifetime;
            var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
            var claims = new DevRoutingTokenClaims(
                DeveloperId: req.DeveloperId.Trim().ToLowerInvariant(),
                AuthorizedServices: req.Services ?? Array.Empty<string>(),
                ExpiresAt: expiresAt
            );
            var token = validator.Issue(claims);

            AppendSessionCookie(ctx, token, lifetime);

            return Results.Ok(new {
                token,
                expiresAt = expiresAt.ToUnixTimeSeconds(),
                authorizedServices = claims.AuthorizedServices
            });
        })
        .WithTags("DevRouting")
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy)
        // CSRF kapalı — endpoint dev-only ve unauthenticated, JSON body istiyor.
        .AllowAnonymous();

        // Re-hydrate: HttpOnly cookie'yi oku, HMAC doğrula, login response'unun
        // aynısını dön. Cookie yoksa/expire/invalid → 401. Toolbar bu endpoint
        // ile reload sonrası session'i geri yükler.
        App.MapGet("/dev/sessions/me", (HttpContext ctx) => {
            if (!ctx.Request.Cookies.TryGetValue(SessionCookieName, out var token) || string.IsNullOrEmpty(token)) {
                return Results.Unauthorized();
            }
            if (!validator.TryValidate(token, out var claims, out _)) {
                // Stale/forged cookie → temizle ki client tekrar tekrar denemesin.
                ctx.Response.Cookies.Delete(SessionCookieName, BuildCookieOptions(ctx));
                return Results.Unauthorized();
            }
            return Results.Ok(new {
                token,
                expiresAt = claims.ExpiresAt.ToUnixTimeSeconds(),
                authorizedServices = claims.AuthorizedServices
            });
        })
        .WithTags("DevRouting")
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy)
        .AllowAnonymous();

        // Logout: cookie temizle. Token kendiliğinden zaten expire eder ama bu
        // endpoint yeni tab/UI'larda da session'in temizlenmesini sağlar.
        App.MapPost("/dev/sessions/logout", (HttpContext ctx) => {
            ctx.Response.Cookies.Delete(SessionCookieName, BuildCookieOptions(ctx));
            return Results.NoContent();
        })
        .WithTags("DevRouting")
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy)
        .AllowAnonymous();

        App.MapGet("/dev/sessions/health", () => Results.Ok(new {
            enabled = options.Enabled,
            secretConfigured = !string.IsNullOrWhiteSpace(options.SharedSecret),
            tokenLifetimeMinutes = (int)options.TokenLifetime.TotalMinutes
        }))
        .WithTags("DevRouting")
        .AllowAnonymous();

        App.Logger.LogInformation(
            "DevSessionsEndpoints mapped. Token lifetime={Lifetime}, secretConfigured={SecretConfigured}",
            options.TokenLifetime, !string.IsNullOrWhiteSpace(options.SharedSecret));
    }

    private static void AppendSessionCookie(HttpContext ctx, string token, TimeSpan lifetime) {
        var opts = BuildCookieOptions(ctx);
        opts.MaxAge = lifetime;
        opts.Expires = DateTimeOffset.UtcNow.Add(lifetime);
        ctx.Response.Cookies.Append(SessionCookieName, token, opts);
    }

    private static CookieOptions BuildCookieOptions(HttpContext ctx) => new() {
        HttpOnly = true,
        // SameSite=Lax: toolbar UI (localhost:3100) → orchestrator (localhost:4000)
        // aynı site sayılır (registrable domain "localhost"). Cross-port credentialed
        // request için yeterli. None gerekmiyor → Secure dev'te zorlanmıyor.
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        Path = "/",
        IsEssential = true
    };

    public sealed record DevSessionRequest(
        string DeveloperId,
        string[]? Services,
        string SharedSecret
    );
}

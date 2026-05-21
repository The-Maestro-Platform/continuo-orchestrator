using Continuo.Observability.Logging;
using Continuo.Shared.Security;
using Microsoft.Extensions.Options;
using Orchestrator.Services;
using Orchestrator.Services.Manifest;

namespace Orchestrator.Hosting.Endpoints;

public sealed class ProxyEndpoints : TechEndpointBase {
    public ProxyEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        var route = App.Map("/{**catchall}", async (HttpContext context, EndpointRegistry registry, IHttpClientFactory httpFactory, UiAppRegistry uiRegistry, IWebHostEnvironment env, ManifestSyncService manifestSync, Orchestrator.Services.Tenants.ITenantContext tenantContext, IOptions<ProxyRequestXssOptions> xssOptions) => {
            if (HttpMethods.IsOptions(context.Request.Method)) {
                // CORS middleware handles preflight headers; if we reach here it's a non-CORS OPTIONS.
                return Results.NoContent();
            }

            // Enable buffering so we can safely rebuild proxy requests if we need to retry due to a stale loopback port.
            context.Request.EnableBuffering();

            await registry.EnsureCacheAsync();
            await uiRegistry.EnsureCacheAsync();

            var info = ProxyRequestInfo.FromHttpContext(context);
            var strictEnvironment = IsStrictEnvironment(env);
            var isBrowserRequest = !string.IsNullOrWhiteSpace(info.Origin) || !string.IsNullOrWhiteSpace(info.Referer);

            var inferredClientApp = TryInferClientAppFromOrigin(uiRegistry, info.Origin) ?? TryInferClientAppFromOrigin(uiRegistry, info.Referer);
            if (string.IsNullOrWhiteSpace(info.ClientApp)) {
                if (!string.IsNullOrWhiteSpace(inferredClientApp)) {
                    info = info with { ClientApp = inferredClientApp };
                }
            }
            else if (!string.IsNullOrWhiteSpace(inferredClientApp) &&
                     !string.Equals(info.ClientApp, inferredClientApp, StringComparison.OrdinalIgnoreCase)) {
                LogProxyDenied(App.Logger, reason: "client-app", info, allowedOrigins: null);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Cookie-prefix fallback. Browser WebSocket handshakes cannot set
            // X-Client-App and env-tenant URLs (`dev-{tenant}.example.local`)
            // strip Referer to origin-only on cross-origin requests, so both
            // host- and path-based inference fall through. The user's auth cookie
            // (`devsup_auth_token`, `admin_auth_token`, …) reveals which UI app
            // they logged into; recover clientApp from its prefix.
            if (string.IsNullOrWhiteSpace(info.ClientApp)) {
                var cookieInferred = AuthCookieResolver.TryInferClientAppFromAuthCookie(
                    context,
                    name => uiRegistry.Resolve(name) != null);
                if (!string.IsNullOrWhiteSpace(cookieInferred)) {
                    info = info with { ClientApp = cookieInferred };
                }
            }

            if (isBrowserRequest && string.IsNullOrWhiteSpace(info.ClientApp)) {
                LogProxyDenied(App.Logger, reason: "unknown-ui", info, allowedOrigins: null);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            context.Items[SystemHttpLogEnrichmentKeys.ClientApp] = info.ClientApp;

            var match = registry.Resolve(info.ClientApp, info.Origin, info.Method, info.Path);
            if (match == null && !strictEnvironment) {
                try {
                    match = await manifestSync.TryResolveFromManifestAsync(
                        info.ClientApp,
                        info.Method,
                        info.Path,
                        context.RequestAborted);
                    if (match != null) {
                        if (App.Logger.IsEnabled(LogLevel.Information)) {
                            App.Logger.LogInformation(
                                "Recovered proxy route from manifest for {ClientApp} {Method} {Path}",
                                info.ClientApp ?? "unknown",
                                info.Method,
                                info.Path);
                        }

                        // TryResolveFromManifestAsync only resolves endpoints, not UI apps.
                        // Load UI apps from the manifest directly into the registry cache
                        // so the UI validation below can find them.
                        try {
                            await manifestSync.TryLoadUiAppsFromManifestAsync(uiRegistry, context.RequestAborted);
                        }
                        catch (Exception uiSyncEx) {
                            App.Logger.LogWarning(uiSyncEx,
                                "Manifest UI app load after route recovery failed for {ClientApp} {Method} {Path}",
                                info.ClientApp ?? "unknown", info.Method, info.Path);
                        }
                    }
                }
                catch (Exception manifestResolveEx) {
                    App.Logger.LogWarning(
                        manifestResolveEx,
                        "Manifest fallback lookup failed for {ClientApp} {Method} {Path}",
                        info.ClientApp ?? "unknown",
                        info.Method,
                        info.Path);
                }
            }

            if (match == null && !strictEnvironment) {
                try {
                    App.Logger.LogWarning(
                        "Proxy route miss for {ClientApp} {Method} {Path}. Triggering manifest sync and retrying once.",
                        info.ClientApp ?? "unknown",
                        info.Method,
                        info.Path);

                    // force=false: effective hash already covers env-var URL drift, so a no-op
                    // sync is a single-row SELECT (no TX, no locks). Forcing here was the source
                    // of the deadlock between this handler and the sync loop's long transaction.
                    var syncCompleted = await manifestSync.TrySyncAsync(
                        force: false,
                        waitTimeout: TimeSpan.FromMilliseconds(250),
                        operationTimeout: TimeSpan.FromSeconds(20),
                        cancellationToken: context.RequestAborted);
                    if (!syncCompleted && App.Logger.IsEnabled(LogLevel.Information)) {
                        App.Logger.LogInformation(
                            "Skipping blocking manifest sync for {ClientApp} {Method} {Path}; another sync is already in progress.",
                            info.ClientApp ?? "unknown",
                            info.Method,
                            info.Path);
                    }

                    await registry.ReloadAsync();
                    await uiRegistry.ReloadAsync();
                    match = registry.Resolve(info.ClientApp ?? string.Empty, info.Origin, info.Method, info.Path);
                }
                catch (Exception syncEx) {
                    App.Logger.LogError(
                        syncEx,
                        "Manifest sync after proxy route miss failed for {ClientApp} {Method} {Path}",
                        info.ClientApp ?? "unknown",
                        info.Method,
                        info.Path);
                }
            }

            if (match == null) {
                LogProxyDenied(App.Logger, reason: "no match", info, allowedOrigins: null);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Handle WebSocket upgrade requests
            if (context.WebSockets.IsWebSocketRequest) {
                return await ProxyWebSocketAsync(context, match, info, strictEnvironment);
            }

            var xssValidation = await ProxyRequestXssGuard.ValidateAsync(
                context.Request,
                match,
                xssOptions.Value,
                context.RequestAborted);
            if (!xssValidation.IsAllowed) {
                App.Logger.LogWarning("Proxy denied by ingress XSS guard reason={Reason} client={ClientApp} method={Method} path={Path}",
                    xssValidation.Reason ?? "unknown", info.ClientApp ?? "unknown", info.Method, info.Path);
                LogProxyDenied(App.Logger, reason: $"xss:{xssValidation.Reason}", info, allowedOrigins: null);
                // 2026-05-14: önceden boş 400 dönüyordu, UI'da generic "API error: 400"
                // görünüyordu. Şimdi structured ProblemDetails ile net mesaj + hint:
                // hangi field (`source`) hangi pattern'e takıldı (`detail`), kullanıcı
                // input'unu temizleyebilsin. Reason format: "<source>:<pattern>" — örn.
                // "body:html-not-allowed" veya "query:filter:dangerous-token".
                var reason = xssValidation.Reason ?? "unknown:unknown";
                var parts = reason.Split(':', 2);
                var source = parts.Length > 0 ? parts[0] : "unknown";
                var pattern = parts.Length > 1 ? parts[1] : "unknown";
                var hint = pattern switch {
                    "html-not-allowed" =>
                        "Request body veya query'de '<tag>'-benzeri içerik bulundu. Secret/config değerinde " +
                        "placeholder (örn. '<accountid>') unutulmuş olabilir — UI'dan ilgili alanı gerçek " +
                        "değerle değiştir ve tekrar dene.",
                    "dangerous-token" =>
                        "Request içeriği tehlikeli pattern içeriyor (<script, javascript:, vb.). Bu bir " +
                        "saldırı şüphesi olarak değerlendirildi.",
                    _ => "Detay için orchestrator log'una bak (trace ID için response header'a)."
                };
                return Results.Problem(
                    title: "XSS tehdit tespit edildi — request engellendi",
                    detail: hint,
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://example.local/errors/xss-guard",
                    extensions: new Dictionary<string, object?> {
                        ["source"] = source,
                        ["pattern"] = pattern,
                        ["path"] = info.Path,
                        ["method"] = info.Method
                    });
            }

            var uiDescriptor = uiRegistry.Resolve(info.ClientApp);
            if (isBrowserRequest && uiDescriptor == null) {
                LogProxyDenied(App.Logger, reason: "unknown-ui", info, allowedOrigins: null);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (uiDescriptor != null && uiDescriptor.AllowedOrigins.Length > 0 && !IsOriginAllowed(uiDescriptor, info.Origin, strictEnvironment)) {
                LogProxyDenied(App.Logger, reason: "origin", info, allowedOrigins: uiDescriptor.AllowedOrigins);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // SignalR hubs and robot simulation endpoints pass tenant context via query parameters;
            // skip customer tenant validation for these paths.
            var skipTenantValidation = info.Path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) ||
                                       info.Path.StartsWith("/robots/sim", StringComparison.OrdinalIgnoreCase);
            if (!skipTenantValidation &&
                !ValidateCustomerTenantContext(context, info, uiDescriptor, strictEnvironment, App.Configuration)) {
                LogProxyDenied(App.Logger, reason: "tenant", info, allowedOrigins: null);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (RequiresCsrfValidation(info.Method, info.Path) &&
                !ShouldSkipAuthForPath(info.Path) &&
                RequiresCsrfValidation(context.Request) &&
                !ValidateCsrf(context.Request)) {
                LogProxyDenied(App.Logger, reason: "csrf", info, allowedOrigins: null);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            Uri? targetUri = null;
            try {
                // Env override: hybrid local-dev senaryosunda dev-up.ps1 local olmayan
                // servisler icin `{SERVICE}__BASE_URL=https://dev-api.example.local`
                // set ediyor → local orchestrator non-local servisleri remote dev
                // orchestrator'a wrap'ler. EndpointRegistry'deki DB base'i (genelde
                // localhost:5080 gibi loopback) override edilir.
                // ServiceLocator (mesajlasma tarafi) ayni mantigi zaten uyguluyor —
                // bkz. EndpointRegistryServiceLocator.ResolveFallbackBaseUrl. Burada
                // proxy layer'inda da ayni override paritesi.
                var effectiveBaseUrl = ResolveProxyTargetOverride(App.Configuration, match.ServiceName) ?? match.ServiceBaseUrl;
                targetUri = BuildTargetUri(effectiveBaseUrl, context.Request.Path, context.Request.QueryString);
                context.Items[SystemHttpLogEnrichmentKeys.TargetUrl] = targetUri.ToString();
                context.Items[SystemHttpLogEnrichmentKeys.TargetService] =
                    !string.IsNullOrWhiteSpace(match.ServiceName)
                        ? match.ServiceName
                        : (Uri.TryCreate(effectiveBaseUrl, UriKind.Absolute, out var baseUri) ? baseUri.Host : effectiveBaseUrl);

                using var reqMessage = CreateProxyRequestMessage(context, targetUri);
                EnsureForwardedHeaders(context, reqMessage, info);
                EnsureTenantHeader(context, reqMessage, tenantContext, info, App.Configuration);
                ApplyProxyAuthorization(App.Configuration, context, reqMessage, uiDescriptor, info.ClientApp, info.Origin);
                ApplyInternalServiceHeaders(App.Configuration, match.ServiceName, reqMessage);

                var client = httpFactory.CreateClient("proxy");
                // SSE/streaming pass-through: HttpClient.Timeout covers the entire
                // request including the body read, so a 30s default kills long
                // Maestro code-generation streams. Detect streaming requests via
                // Accept header and disable the per-request timeout — we still
                // honour client disconnects through context.RequestAborted.
                var isStreamingRequest = IsStreamingRequest(context);
                client.Timeout = isStreamingRequest
                    ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromMilliseconds(match.TimeoutMs ?? 30000);

                if (App.Logger.IsEnabled(LogLevel.Information)) {
                    App.Logger.LogInformation("Proxy allow {Method} {Path} -> {Target} client={ClientApp} origin={Origin} roles={Roles} policies={Policies} ip={Ip}",
                        info.Method, info.Path, targetUri, info.ClientApp ?? "unknown", info.Origin ?? "none",
                        match.Roles.Count > 0 ? string.Join(",", match.Roles) : "none",
                        match.Policies.Count > 0 ? string.Join(",", match.Policies) : "none",
                        info.RemoteIp ?? "unknown");
                }

                using var resp = await SendProxyRequestAsync(
                    context,
                    client,
                    targetUri,
                    reqMessage,
                    uiDescriptor,
                    info,
                    match.ServiceName,
                    App.Configuration);
                context.Response.StatusCode = (int)resp.StatusCode;
                CopyResponseHeaders(context.Response, resp);

                if (!resp.IsSuccessStatusCode) {
                    App.Logger.LogWarning("Proxy backend returned {Status} for {ClientApp} {Method} {Path} -> {Target}",
                        resp.StatusCode, info.ClientApp, info.Method, info.Path, targetUri);
                }

                await resp.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                return Results.Empty;
            }
            catch (UriFormatException ex) {
                App.Logger.LogError(ex, "Proxy configuration error: invalid base URL for {Service} on {Method} {Path}. BaseUrl='{BaseUrl}'",
                    match.ServiceName ?? "unknown", info.Method, info.Path, match.ServiceBaseUrl);
                context.Items[SystemHttpLogEnrichmentKeys.Error] = ex.ToString();
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            catch (HttpRequestException ex) {
                // Dev-friendly recovery for local runs (AppHost) where loopback service ports can change across restarts.
                // If the target is loopback and connection is refused, force-sync endpoint_proxy.json into the registry and retry once.
                if (!strictEnvironment &&
                    targetUri != null &&
                    IsLoopbackHost(targetUri.Host) &&
                    IsConnectionRefused(ex)) {
                    try {
                        App.Logger.LogWarning(ex, "Proxy connection refused for {ClientApp} {Method} {Path} -> {Target}. Triggering manifest sync and retrying once.",
                            info.ClientApp, info.Method, info.Path, targetUri);

                        // force=false: manifest+URL hash already detects loopback port changes
                        // because ServiceUrlResolver pulls fresh env-vars into the hash input.
                        var syncCompleted = await manifestSync.TrySyncAsync(
                            force: false,
                            waitTimeout: TimeSpan.FromMilliseconds(250),
                            operationTimeout: TimeSpan.FromSeconds(20),
                            cancellationToken: context.RequestAborted);
                        if (!syncCompleted && App.Logger.IsEnabled(LogLevel.Information)) {
                            App.Logger.LogInformation(
                                "Skipping blocking manifest sync after connection refusal for {ClientApp} {Method} {Path}; another sync is already in progress.",
                                info.ClientApp ?? "unknown",
                                info.Method,
                                info.Path);
                        }

                        await registry.ReloadAsync();

                        var refreshed = registry.Resolve(info.ClientApp ?? string.Empty, info.Origin, info.Method, info.Path);
                        if (refreshed != null) {
                            var refreshedBaseUrl = ResolveProxyTargetOverride(App.Configuration, refreshed.ServiceName) ?? refreshed.ServiceBaseUrl;
                            var refreshedTarget = BuildTargetUri(refreshedBaseUrl, context.Request.Path, context.Request.QueryString);
                            context.Items[SystemHttpLogEnrichmentKeys.TargetUrl] = refreshedTarget.ToString();
                            context.Items[SystemHttpLogEnrichmentKeys.TargetService] =
                                !string.IsNullOrWhiteSpace(refreshed.ServiceName)
                                    ? refreshed.ServiceName
                                    : (Uri.TryCreate(refreshedBaseUrl, UriKind.Absolute, out var baseUri) ? baseUri.Host : refreshedBaseUrl);

                            using var retryMessage = CreateProxyRequestMessage(context, refreshedTarget);
                            ApplyProxyAuthorization(App.Configuration, context, retryMessage, uiDescriptor, info.ClientApp, info.Origin);
                            ApplyInternalServiceHeaders(App.Configuration, refreshed.ServiceName, retryMessage);

                            var retryClient = httpFactory.CreateClient("proxy");
                            retryClient.Timeout = IsStreamingRequest(context)
                                ? Timeout.InfiniteTimeSpan
                                : TimeSpan.FromMilliseconds(refreshed.TimeoutMs ?? 30000);

                            using var retryResp = await retryClient.SendAsync(retryMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                            context.Response.StatusCode = (int)retryResp.StatusCode;
                            CopyResponseHeaders(context.Response, retryResp);
                            await retryResp.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                            return Results.Empty;
                        }
                    }
                    catch (Exception retryEx) {
                        App.Logger.LogError(retryEx, "Proxy retry after manifest sync failed for {ClientApp} {Method} {Path}", info.ClientApp, info.Method, info.Path);
                    }
                }

                App.Logger.LogError(ex, "Proxy error for {ClientApp} {Method} {Path} -> {Target}", info.ClientApp, info.Method, info.Path, targetUri?.ToString() ?? "(unresolved)");
                context.Items[SystemHttpLogEnrichmentKeys.Error] = ex.ToString();
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            catch (TaskCanceledException ex) {
                App.Logger.LogWarning(ex, "Proxy timeout for {ClientApp} {Method} {Path} -> {Target}", info.ClientApp, info.Method, info.Path, targetUri?.ToString() ?? "(unresolved)");
                context.Items[SystemHttpLogEnrichmentKeys.Error] = ex.ToString();
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex) {
                App.Logger.LogError(ex, "Proxy error for {ClientApp} {Method} {Path} -> {Target}", info.ClientApp, info.Method, info.Path, targetUri?.ToString() ?? "(unresolved)");
                context.Items[SystemHttpLogEnrichmentKeys.Error] = ex.ToString();
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        })
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy);
    }

    private static void EnsureTenantHeader(
        HttpContext context,
        HttpRequestMessage message,
        Orchestrator.Services.Tenants.ITenantContext tenantContext,
        ProxyRequestInfo info,
        IConfiguration configuration) {
        // X-Env-Prefix ve X-M2M-API-KEY orchestrator-controlled trust sinyalleri.
        // CreateProxyRequestMessage tüm inbound header'ları downstream'e kopyalıyor —
        // browser bu header'ları spoof etmemesi için her durumda önce strip ediyoruz,
        // sonra koşullarına göre tekrar ekliyoruz. Strip'i function'ın başında ve
        // tenant slug logic'inin early return'lerinden ÖNCE yapmak şart, yoksa
        // tenant header zaten set edilmişken X-Env-Prefix spoof'u sızabilirdi.
        message.Headers.Remove("X-Env-Prefix");
        message.Headers.Remove("X-M2M-API-KEY");

        // X-Env-Prefix: URL host'unun ilk segmenti env-prefix ile başlıyorsa
        // ("dev-foo", "staging-foo"), prefix'i header'a yaz. Auth-api bu sinyali
        // env-prefix URL'lerinde Customer login'i reddetmek için kullanır
        // (env-master + env-tenant her ikisi de staff-only).
        //
        // Trust signaling: header'ın yanına M2M API key de ekleniyor — auth-api
        // X-Env-Prefix'i ancak X-M2M-API-KEY doğru gelirse honour ediyor. Browser
        // kullanıcısı M2M key'i göremez, doğrudan auth-api'ye giden istekler bu
        // header çiftiyle gelmez.
        var envPrefix = TryInferEnvPrefix(info.Origin) ?? TryInferEnvPrefix(info.Referer);
        if (!string.IsNullOrWhiteSpace(envPrefix)) {
            message.Headers.TryAddWithoutValidation("X-Env-Prefix", envPrefix);

            var m2mKey = configuration["M2M:ApiKey"]
                         ?? configuration["M2M__API_KEY"]
                         ?? configuration["M2M_API_KEY"];
            if (!string.IsNullOrWhiteSpace(m2mKey)) {
                message.Headers.TryAddWithoutValidation("X-M2M-API-KEY", m2mKey);
            }
        }

        var resolvedTenantSlug =
            !string.IsNullOrWhiteSpace(tenantContext.TenantSlug)
                ? tenantContext.TenantSlug
                : (TryInferTenantSlug(info.Origin, configuration) ?? TryInferTenantSlug(info.Referer, configuration));

        // Only hydrate tenant context for browser calls. Service-to-service calls should always pass tenant context explicitly.
        if (string.IsNullOrWhiteSpace(info.Origin) && string.IsNullOrWhiteSpace(info.Referer)) {
            return;
        }

        var incomingTenantSlug = context.Request.Headers["X-Tenant-Slug"].FirstOrDefault();
        var incomingTenantCode = context.Request.Headers["X-Tenant-Code"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incomingTenantSlug) || !string.IsNullOrWhiteSpace(incomingTenantCode)) {
            return;
        }

        // If the caller already provides tenant information in query parameters, do not override.
        var query = context.Request.Query;
        if (query.ContainsKey("tenantId") || query.ContainsKey("tenantSlug") || query.ContainsKey("tenantCode")) {
            return;
        }

        if (string.IsNullOrWhiteSpace(resolvedTenantSlug)) {
            return;
        }

        // Propagate the resolved tenant to downstream services (Origin/Referer are not forwarded).
        message.Headers.Remove("X-Tenant-Slug");
        message.Headers.TryAddWithoutValidation("X-Tenant-Slug", resolvedTenantSlug);
    }

    private static readonly string[] EnvHostPrefixes = { "dev-", "staging-", "stg-", "prod-", "production-", "test-" };

    // Keep in sync with Continuo.Shared.Security.TenantResolution.AppSuffixes and
    // ui/packages/api-client/src/envResolver.ts::APP_SUFFIXES.
    private static readonly string[] AppSuffixes = {
        "-web",
        "-admin",
        "-ops",
        "-mobile",
        "-public",
        "-console",
        "-kiosk",
        "-pos",
        "-qr",
        "-support",
        "-tcc",
        "-tablet",
        "-ui"
    };

    private static string? TryInferEnvPrefix(string? originOrReferer) {
        if (string.IsNullOrWhiteSpace(originOrReferer)) {
            return null;
        }
        if (!Uri.TryCreate(originOrReferer.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) {
            return null;
        }
        var host = (uri.Host ?? string.Empty).ToLowerInvariant();
        var firstSegment = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstSegment)) {
            return null;
        }
        foreach (var prefix in EnvHostPrefixes) {
            if (firstSegment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                return prefix.TrimEnd('-');
            }
        }
        return null;
    }

    private static bool IsLoopbackHost(string? host) {
        if (string.IsNullOrWhiteSpace(host)) {
            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage> SendProxyRequestAsync(
        HttpContext context,
        HttpClient client,
        Uri targetUri,
        HttpRequestMessage initialMessage,
        UiAppDescriptor? uiDescriptor,
        ProxyRequestInfo info,
        string? serviceName,
        IConfiguration configuration) {
        // Some services (e.g. SignalR hubs) may not be ready during app startup; avoid transient 502s caused by
        // connection-refused by retrying a couple of times for negotiate calls with no request body.
        var shouldRetry =
            info.Path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) &&
            info.Path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) &&
            (context.Request.ContentLength ?? 0) == 0 &&
            !context.Request.Headers.ContainsKey("Transfer-Encoding");

        if (!shouldRetry) {
            return await client.SendAsync(initialMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }

        var delays = new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500) };

        for (var attempt = 0; attempt <= delays.Length; attempt++) {
            HttpRequestMessage reqMessage;
            if (attempt == 0) {
                reqMessage = initialMessage;
            }
            else {
                reqMessage = CreateProxyRequestMessage(context, targetUri);
                ApplyProxyAuthorization(configuration, context, reqMessage, uiDescriptor, info.ClientApp, info.Origin);
                ApplyInternalServiceHeaders(configuration, serviceName, reqMessage);
            }

            try {
                return await client.SendAsync(reqMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (HttpRequestException ex) when (attempt < delays.Length && IsConnectionRefused(ex)) {
                await Task.Delay(delays[attempt], context.RequestAborted);
            }
            finally {
                if (attempt > 0) {
                    reqMessage.Dispose();
                }
            }
        }

        // Should be unreachable; fall back to sending without retry.
        return await client.SendAsync(initialMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    }

    private static bool IsStreamingRequest(HttpContext context) {
        // Treat any inbound request that explicitly accepts text/event-stream as
        // a streaming proxy hop (e.g. Maestro chat SSE). The orchestrator already
        // uses HttpCompletionOption.ResponseHeadersRead + CopyToAsync, so the
        // only thing that breaks SSE is the HttpClient.Timeout deadline — this
        // signal lets the caller opt into a long-running stream.
        var accept = context.Request.Headers.Accept;
        for (var i = 0; i < accept.Count; i++) {
            var value = accept[i];
            if (!string.IsNullOrEmpty(value) &&
                value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }
        return false;
    }

    private static bool IsConnectionRefused(HttpRequestException ex) {
        if (ex.InnerException is System.Net.Sockets.SocketException socketEx) {
            return socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused;
        }

        return false;
    }

    private static void ApplyInternalServiceHeaders(IConfiguration configuration, string? serviceName, HttpRequestMessage message) {
        if (string.IsNullOrWhiteSpace(serviceName)) {
            return;
        }

        // Payments API is protected with an internal service key. The orchestrator injects the key
        // so UI callers never see secrets and cannot bypass gateway security controls.
        if (!serviceName.Equals("payment-api", StringComparison.OrdinalIgnoreCase) &&
            !serviceName.Equals("payments", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        var headerName =
            configuration["PaymentSecurity:HeaderName"] ??
            configuration["PAYMENT_SECURITY__HEADER_NAME"] ??
            "X-Service-Key";

        var internalKey =
            configuration["PaymentSecurity:InternalKey"] ??
            configuration["PAYMENT_SECURITY__INTERNAL_KEY"];

        if (string.IsNullOrWhiteSpace(internalKey)) {
            return;
        }

        // Override any incoming value (UI must not provide this header).
        message.Headers.Remove(headerName);
        message.Headers.TryAddWithoutValidation(headerName, internalKey);
    }

    private async Task<IResult> ProxyWebSocketAsync(HttpContext context, dynamic match, ProxyRequestInfo info, bool strictEnvironment) {
        try {
            // HTTP yolu (yukarıdaki SendProxyRequestAsync) `ResolveProxyTargetOverride`
            // ile `{SERVICE}__BASE_URL` env-var'ını okuyup manifest'in canonical URL'ini
            // override ediyor (dev'de `notification-api:80` → `dev-notification-api:5000`).
            // WS yolu bu override'ı atlıyordu → manifest'teki canonical hostname'e bağlanmaya
            // çalışıp DNS fail/connection-refused alıyor, sonra 502 dönüyordu. Aynı override'ı
            // burada da uygula ki dev/staging'deki prefix'li container isimleri çözülsün.
            var effectiveBaseUrl = ResolveProxyTargetOverride(App.Configuration, (string?)match.ServiceName) ?? (string)match.ServiceBaseUrl;
            var targetUri = BuildTargetUri(effectiveBaseUrl, context.Request.Path, context.Request.QueryString);
            var wsTargetUri = targetUri.ToString().Replace("http://", "ws://").Replace("https://", "wss://");

            var logger = App.Logger;
            var path = info.Path;
            var clientApp = info.ClientApp ?? "unknown";
            var origin = info.Origin ?? "none";
            if (logger.IsEnabled(LogLevel.Information)) {
                logger.LogInformation("WebSocket proxy {Path}{QueryString} -> {Target} client={ClientApp} origin={Origin}",
                    path, context.Request.QueryString.Value ?? "", wsTargetUri, clientApp, origin);
            }

            var clientSocket = new System.Net.WebSockets.ClientWebSocket();
            System.Net.WebSockets.WebSocket? serverSocket = null;
            try {
                // Copy headers to backend WebSocket
                if (context.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols)) {
                    foreach (var protocol in protocols) {
                        if (!string.IsNullOrWhiteSpace(protocol)) {
                            clientSocket.Options.AddSubProtocol(protocol);
                        }
                    }
                }

                // Pass tenant context to backend service
                var tenantContext = context.RequestServices.GetService<Orchestrator.Services.Tenants.ITenantContext>();
                var queryTenantId = context.Request.Query["tenantId"].FirstOrDefault();
                var queryTenantSlug = context.Request.Query["tenantSlug"].FirstOrDefault();
                var resolvedTenantSlug = !string.IsNullOrWhiteSpace(tenantContext?.TenantSlug)
                    ? tenantContext.TenantSlug
                    : (queryTenantSlug ?? queryTenantId);
                var tenantSource = !string.IsNullOrWhiteSpace(tenantContext?.TenantSlug)
                    ? "tenant-context"
                    : (!string.IsNullOrWhiteSpace(queryTenantSlug) || !string.IsNullOrWhiteSpace(queryTenantId)
                        ? "query-fallback"
                        : "missing");

                if (logger.IsEnabled(LogLevel.Information)) {
                    logger.LogInformation(
                        "WebSocket tenant context: source={Source}, TenantSlug={TenantSlug}, TenantId={TenantId}, QueryTenantId={QueryTenantId}, QueryTenantSlug={QueryTenantSlug}",
                        tenantSource,
                        tenantContext?.TenantSlug ?? "null",
                        tenantContext?.TenantId?.ToString() ?? "null",
                        queryTenantId ?? "null",
                        queryTenantSlug ?? "null");
                }

                if (!string.IsNullOrWhiteSpace(resolvedTenantSlug)) {
                    clientSocket.Options.SetRequestHeader("X-Tenant-Slug", resolvedTenantSlug);
                }
                if (tenantContext?.TenantId.HasValue == true) {
                    clientSocket.Options.SetRequestHeader("X-Tenant-Id", tenantContext.TenantId.Value.ToString());
                }

                // Env-prefix + M2M trust pair (HTTP proxy'deki EnsureTenantHeader ile aynı.)
                // Browser WebSocket API custom header set edemediğinden orchestrator'un
                // backend'e bu sinyalleri kendi enjekte etmesi gerekiyor — yoksa
                // notification-api gibi env-prefix-aware servisler 403 düşürüyor.
                var envPrefix = TryInferEnvPrefix(info.Origin) ?? TryInferEnvPrefix(info.Referer);
                if (!string.IsNullOrWhiteSpace(envPrefix)) {
                    clientSocket.Options.SetRequestHeader("X-Env-Prefix", envPrefix);
                    var m2mKey = App.Configuration["M2M:ApiKey"]
                                 ?? App.Configuration["M2M__API_KEY"]
                                 ?? App.Configuration["M2M_API_KEY"];
                    if (!string.IsNullOrWhiteSpace(m2mKey)) {
                        clientSocket.Options.SetRequestHeader("X-M2M-API-KEY", m2mKey);
                    }
                }

                // Client-app: HTTP'de header/JWT'den geliyor, browser WS'de yok →
                // orchestrator info.ClientApp (origin'den çıkarılmış) backend'e taşınır.
                if (!string.IsNullOrWhiteSpace(info.ClientApp)) {
                    clientSocket.Options.SetRequestHeader("X-Client-App", info.ClientApp);
                }

                // Auth: WS upgrade browser cookie'leri otomatik gönderir, ama biz
                // orchestrator → backend bağlantısını kendimiz açıyoruz. Cookie +
                // Authorization manuel olarak forward edilmeli.
                //
                // KRİTİK: Backend service'ler (notification-api, security-api, …)
                // JwtBearer scheme kullanıyor — auth_token cookie'sini OKUMAZ. HTTP
                // proxy'de `ApplyProxyAuthorization` cookie'yi `Authorization: Bearer
                // {jwt}` header'ına çeviriyor; WS tarafında da aynı dönüşüm yapılmalı,
                // aksi halde notification-api 401 → ClientWebSocket.ConnectAsync
                // throw → proxy 502 ve UI'da `WebSocket connection ... failed` hatası.
                var cookieHeader = context.Request.Headers["Cookie"].ToString();
                if (!string.IsNullOrWhiteSpace(cookieHeader)) {
                    clientSocket.Options.SetRequestHeader("Cookie", cookieHeader);
                }
                var authHeader = context.Request.Headers["Authorization"].ToString();
                if (string.IsNullOrWhiteSpace(authHeader)) {
                    // Browser WS Authorization gönderemediği için cookie'yi JWT'ye çevir.
                    // uiDescriptor WS akışında resolve edilmiyor; clientApp prefix'i
                    // AuthCookieResolver içindeki guess fallback'leriyle yeterli.
                    var uiRegistry = context.RequestServices.GetService<UiAppRegistry>();
                    var uiDescriptor = uiRegistry?.Resolve(info.ClientApp);
                    if (AuthCookieResolver.TryResolveAuthToken(context, uiDescriptor, info.ClientApp, out var cookieToken) &&
                        !string.IsNullOrWhiteSpace(cookieToken)) {
                        authHeader = $"Bearer {cookieToken}";
                    }
                }
                if (!string.IsNullOrWhiteSpace(authHeader)) {
                    clientSocket.Options.SetRequestHeader("Authorization", authHeader);
                }

                await clientSocket.ConnectAsync(new Uri(wsTargetUri), context.RequestAborted);
                serverSocket = await context.WebSockets.AcceptWebSocketAsync();

                var clientToServer = RelayWebSocketAsync(clientSocket, serverSocket, "client->server", context.RequestAborted);
                var serverToClient = RelayWebSocketAsync(serverSocket, clientSocket, "server->client", context.RequestAborted);

                await Task.WhenAny(clientToServer, serverToClient);

                if (clientSocket.State == System.Net.WebSockets.WebSocketState.Open) {
                    await clientSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Proxy closed", CancellationToken.None);
                }

                if (serverSocket.State == System.Net.WebSockets.WebSocketState.Open) {
                    await serverSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Proxy closed", CancellationToken.None);
                }

                await Task.WhenAll(clientToServer, serverToClient);
            }
            finally {
                serverSocket?.Dispose();
                clientSocket.Dispose();
            }

            return Results.Empty;
        }
        catch (Exception ex) {
            var logger = App.Logger;
            var path = info.Path;
            logger.LogError(ex, "WebSocket proxy error for {Path}", path);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private async Task RelayWebSocketAsync(System.Net.WebSockets.WebSocket source, System.Net.WebSockets.WebSocket destination, string direction, CancellationToken ct) {
        var buffer = new byte[1024 * 4];
        try {
            while (!ct.IsCancellationRequested && source.State == System.Net.WebSockets.WebSocketState.Open) {
                var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) {
                    if (destination.State == System.Net.WebSockets.WebSocketState.Open) {
                        await destination.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closed by peer", ct);
                    }

                    break;
                }

                if (destination.State == System.Net.WebSockets.WebSocketState.Open) {
                    await destination.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        ct);
                }
            }
        }
        catch (OperationCanceledException) {
            // Expected on cancellation
        }
        catch (Exception ex) {
            var logger = App.Logger;
            logger.LogWarning(ex, "WebSocket relay error ({Direction})", direction);
        }
    }

    private static void LogProxyDenied(ILogger logger, string reason, ProxyRequestInfo info, string[]? allowedOrigins) {
        if (string.Equals(reason, "origin", StringComparison.OrdinalIgnoreCase)) {
            logger.LogWarning(
                "Proxy denied (origin) {Method} {Path} client={ClientApp} origin={Origin} allowed={Allowed} ip={Ip}",
                info.Method,
                info.Path,
                string.IsNullOrWhiteSpace(info.ClientApp) ? "unknown" : info.ClientApp,
                string.IsNullOrWhiteSpace(info.Origin) ? "none" : info.Origin,
                allowedOrigins == null ? string.Empty : string.Join(",", allowedOrigins),
                info.RemoteIp ?? "unknown");
            return;
        }

        logger.LogWarning(
            "Proxy denied ({Reason}) {Method} {Path} client={ClientApp} origin={Origin} referer={Referer} ip={Ip} ua={UserAgent}",
            reason,
            info.Method,
            info.Path,
            string.IsNullOrWhiteSpace(info.ClientApp) ? "unknown" : info.ClientApp,
            string.IsNullOrWhiteSpace(info.Origin) ? "none" : info.Origin,
            string.IsNullOrWhiteSpace(info.Referer) ? "none" : info.Referer,
            info.RemoteIp ?? "unknown",
            string.IsNullOrWhiteSpace(info.UserAgent) ? "unknown" : info.UserAgent);
    }

    private static string? TryInferClientAppFromOrigin(UiAppRegistry uiRegistry, string? originOrReferer) {
        var normalized = NormalizeOrigin(originOrReferer);
        if (string.IsNullOrWhiteSpace(normalized)) {
            return null;
        }

        // First: try deterministic host-based inference. This avoids ambiguity when many UI apps use a shared wildcard
        // allowed origin like https://*.example.local.
        var hostBased = TryInferClientAppFromHost(uiRegistry, normalized);
        if (!string.IsNullOrWhiteSpace(hostBased)) {
            return hostBased;
        }

        // Then: path-prefix fallback for env-tenant routing scheme. Uses raw URL
        // (NormalizeOrigin strips path) so it only resolves when called with the
        // Referer header. WS handshakes can't carry X-Client-App; this lets
        // dev-{tenant}.example.local/ops/... still map to continuo-ops-ui.
        var pathBased = TryInferClientAppFromPath(uiRegistry, originOrReferer);
        if (!string.IsNullOrWhiteSpace(pathBased)) {
            return pathBased;
        }

        string? match = null;
        string? exactMatch = null;
        foreach (var app in uiRegistry.GetAll()) {
            foreach (var allowed in app.AllowedOrigins) {
                if (!IsOriginMatch(allowed, normalized)) {
                    continue;
                }

                // Prefer exact matches (no wildcard) when available.
                if (!allowed.Contains('*', StringComparison.Ordinal) &&
                    string.Equals(NormalizeOrigin(allowed), normalized, StringComparison.OrdinalIgnoreCase)) {
                    if (exactMatch != null && !string.Equals(exactMatch, app.Name, StringComparison.OrdinalIgnoreCase)) {
                        return null; // still ambiguous even with exact matches
                    }
                    exactMatch = app.Name;
                }

                if (match != null && !string.Equals(match, app.Name, StringComparison.OrdinalIgnoreCase)) {
                    // Ambiguous wildcard match; attempt to resolve via host mapping.
                    var resolved = TryInferClientAppFromHost(uiRegistry, normalized);
                    return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
                }

                match = app.Name;
                break;
            }
        }

        return exactMatch ?? match;
    }

    private static string? TryInferClientAppFromHost(UiAppRegistry uiRegistry, string normalizedOrigin) {
        if (!Uri.TryCreate(normalizedOrigin, UriKind.Absolute, out var uri)) {
            return null;
        }

        var host = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) {
            return null;
        }

        static string? FirstExisting(UiAppRegistry registry, params string[] candidates) {
            foreach (var candidate in candidates) {
                if (string.IsNullOrWhiteSpace(candidate)) {
                    continue;
                }
                if (registry.Resolve(candidate) != null) {
                    return candidate;
                }
            }
            return null;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) {
            return uri.Port switch {
                3100 => FirstExisting(uiRegistry, "console-admin"),
                3101 => FirstExisting(uiRegistry, "continuo-ops-ui", "console-operations"),
                3200 => FirstExisting(uiRegistry, "qrmenu-web"),
                3201 => FirstExisting(uiRegistry, "kiosk-ui"),
                3202 => FirstExisting(uiRegistry, "pos-offline"),
                3300 => FirstExisting(uiRegistry, "qrmenu-mobile"),
                3400 => FirstExisting(uiRegistry, "public-web"),
                3500 => FirstExisting(uiRegistry, "tcc-ui"),
                3600 => FirstExisting(uiRegistry, "tcc-ops-ui"),
                3700 => FirstExisting(uiRegistry, "continuo-web"),
                4200 => FirstExisting(uiRegistry, "maestro-console"),
                _ => null
            };
        }

        // First try the canonical multi-tenant host pattern: {env}.{app}.example.local
        // (e.g. staging.qrmenu-web.example.local → app="qrmenu-web"). This is more
        // accurate than substring rules below — it avoids "-web." catching qrmenu-web
        // when the actual app is qrmenu-web (was mis-resolved to continuo-web).
        var segments = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 4 && segments[^3].Equals("default", StringComparison.OrdinalIgnoreCase)) {
            var appCandidate = segments[^4];
            if (uiRegistry.Resolve(appCandidate) != null) {
                return appCandidate;
            }
        }

        // Keep mappings conservative and stable: infer based on host patterns that are already enforced at ingress.
        if (host.Contains("-admin.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "console-admin");
        }

        if (host.Contains("-ops.", StringComparison.OrdinalIgnoreCase) || host.StartsWith("tc-ops.", StringComparison.OrdinalIgnoreCase)) {
            // Different environments may use different client app keys.
            return FirstExisting(uiRegistry, "continuo-ops-ui", "console-operations");
        }

        if (host.StartsWith("sup.", StringComparison.OrdinalIgnoreCase) || host.Contains("-sup.", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("dev.", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("dev-support.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "maestro-console");
        }

        // Env-tenant URLs (`dev-{tenant}.example.local`) host birden fazla UI app'i path-prefix
        // ile servis ediyor (`/admin` → console-admin, `/devops` → maestro-console, `/m` → qrmenu-mobile, …).
        // Host-based inference bu durumda spesifik bir app döndüremez; null döndür ki path-based
        // fallback (TryInferClientAppFromPath) request path'inden doğru app'i çıkarsın.
        var firstHostSegment = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var hasEnvPrefix = EnvHostPrefixes.Any(p => firstHostSegment.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (hasEnvPrefix) {
            var afterPrefix = EnvHostPrefixes
                .Where(p => firstHostSegment.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                .Select(p => firstHostSegment[p.Length..])
                .FirstOrDefault() ?? string.Empty;
            // env-prefix + app-suffix (`dev-console-admin`, `dev-continuo-ops-ui`) env-master'dır;
            // alttaki substring kuralları (`-admin.`, `-ops.`) zaten yakalıyor. Env-tenant
            // (`dev-tenant1`, `dev-tenant2`) yalnız path ile resolve edilmeli.
            var isEnvMaster = AppSuffixes.Any(s => afterPrefix.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            if (!isEnvMaster && !string.IsNullOrWhiteSpace(afterPrefix)) {
                return null;
            }
        }

        if (host.Contains("-mobile.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "qrmenu-mobile");
        }

        if (host.Contains("-qr.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "qrmenu-web");
        }

        if (host.Contains("-kiosk.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "kiosk-ui");
        }

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "public-web");
        }

        if (host.Contains("-web.", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "continuo-web");
        }

        // Root domain portal UI.
        if (host.Equals("example.local", StringComparison.OrdinalIgnoreCase)) {
            return FirstExisting(uiRegistry, "continuo-web");
        }

        return null;
    }

    // Path-prefix fallback for env-tenant routing scheme:
    //   {env}-{tenant}.example.local/<prefix>/...  →  UI app
    // Traefik routes `/admin`, `/ops`, `/devops`, `/menu`, `/m`, `/web`, `/tcc`
    // to distinct UI apps under the same tenant host. Origin header has no
    // path so this only resolves when called with Referer; it gives the WS
    // proxy (which can't read X-Client-App from browsers) a way to figure
    // out the UI when host-only inference is ambiguous.
    private static string? TryInferClientAppFromPath(UiAppRegistry uiRegistry, string? originOrReferer) {
        if (string.IsNullOrWhiteSpace(originOrReferer)) {
            return null;
        }
        if (!Uri.TryCreate(originOrReferer.Trim(), UriKind.Absolute, out var uri)) {
            return null;
        }
        var host = (uri.Host ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host) || !host.EndsWith(".example.local", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        var firstSegment = host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var hasEnvTenantPrefix = false;
        foreach (var prefix in EnvHostPrefixes) {
            if (firstSegment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                hasEnvTenantPrefix = true;
                break;
            }
        }
        if (!hasEnvTenantPrefix) {
            return null;
        }
        var path = uri.AbsolutePath ?? string.Empty;
        static string? FirstExisting(UiAppRegistry registry, params string[] candidates) {
            foreach (var candidate in candidates) {
                if (string.IsNullOrWhiteSpace(candidate)) {
                    continue;
                }
                if (registry.Resolve(candidate) != null) {
                    return candidate;
                }
            }
            return null;
        }
        string? PathMatch(string segment, params string[] candidates) {
            if (path.Equals("/" + segment, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/" + segment + "/", StringComparison.OrdinalIgnoreCase)) {
                return FirstExisting(uiRegistry, candidates);
            }
            return null;
        }
        return PathMatch("ops", "continuo-ops-ui", "console-operations")
            ?? PathMatch("admin", "console-admin")
            ?? PathMatch("devops", "maestro-console")
            ?? PathMatch("menu", "qrmenu-web")
            ?? PathMatch("m", "qrmenu-mobile")
            ?? PathMatch("web", "public-web")
            ?? PathMatch("tcc", "tcc-ui");
    }

    private static string NormalizeOrigin(string? originOrReferer) {
        if (string.IsNullOrWhiteSpace(originOrReferer)) {
            return string.Empty;
        }

        var raw = originOrReferer.Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) {
            return raw;
        }

        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{portPart}";
    }

    private static void EnsureForwardedHeaders(HttpContext context, HttpRequestMessage message, ProxyRequestInfo info) {
        // Only enrich forwarded headers for browser calls. Service-to-service calls should set their own values.
        if (string.IsNullOrWhiteSpace(info.Origin) && string.IsNullOrWhiteSpace(info.Referer)) {
            return;
        }

        if (!message.Headers.Contains("X-Forwarded-Host")) {
            var host = context.Request.Host.Value;
            if (!string.IsNullOrWhiteSpace(host)) {
                message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
            }
        }

        if (!message.Headers.Contains("X-Forwarded-Proto")) {
            var scheme = context.Request.Scheme;
            if (!string.IsNullOrWhiteSpace(scheme)) {
                message.Headers.TryAddWithoutValidation("X-Forwarded-Proto", scheme);
            }
        }
    }

    private static bool ValidateCustomerTenantContext(
        HttpContext context,
        ProxyRequestInfo info,
        UiAppDescriptor? uiDescriptor,
        bool strictEnvironment,
        IConfiguration configuration) {
        if (!strictEnvironment || uiDescriptor?.CustomerFacing != true) {
            return true;
        }

        var inferred = TryInferTenantSlug(info.Origin, configuration) ?? TryInferTenantSlug(info.Referer, configuration);
        if (string.IsNullOrWhiteSpace(inferred)) {
            return true;
        }

        var incomingTenantSlug = context.Request.Headers["X-Tenant-Slug"].FirstOrDefault();
        var incomingTenantCode = context.Request.Headers["X-Tenant-Code"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(incomingTenantCode)) {
            return false; // customer apps must not select tenants by code
        }

        var query = context.Request.Query;
        if (query.ContainsKey("tenantId") || query.ContainsKey("tenantCode")) {
            return false; // customer apps must not select tenants by id/code
        }

        var queryTenantSlug = query.TryGetValue("tenantSlug", out var slug) ? slug.FirstOrDefault() : null;
        var requestedTenantSlug = incomingTenantSlug ?? queryTenantSlug;

        if (!string.IsNullOrWhiteSpace(requestedTenantSlug) &&
            !string.Equals(requestedTenantSlug, inferred, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return true;
    }

    private static string? TryInferTenantSlug(string? originOrReferer, IConfiguration configuration) {
        var defaultSlug =
            configuration["NEXT_PUBLIC_DEFAULT_TENANT_SLUG"] ??
            configuration["DEFAULT_TENANT_SLUG"] ??
            "default";

        if (string.IsNullOrWhiteSpace(originOrReferer)) {
            return null;
        }

        var raw = originOrReferer.Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) {
            return null;
        }

        var host = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host) || IsLoopbackHost(host)) {
            return defaultSlug;
        }

        // Delegate to the canonical parser in Continuo.Shared so that orchestrator,
        // backend middleware, and UI clients all agree on the same mapping.
        // Returns null for env-master URLs (e.g. dev-console-admin); fall back to default.
        return TenantResolution.ExtractFromHost(host) ?? defaultSlug;
    }

    private static bool IsOriginMatch(string allowed, string origin) {
        if (string.IsNullOrWhiteSpace(allowed) || string.IsNullOrWhiteSpace(origin)) {
            return false;
        }

        if (string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!allowed.Contains('*', StringComparison.Ordinal)) {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) {
            return false;
        }

        if (!Uri.TryCreate(allowed.Replace("*.", "wildcard.", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var allowedUri)) {
            return false;
        }

        if (!string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (!allowedUri.IsDefaultPort && originUri.Port != allowedUri.Port) {
            return false;
        }

        var allowedHost = allowedUri.Host;
        if (!allowedHost.StartsWith("wildcard.", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var suffix = allowedHost["wildcard.".Length..];
        if (string.IsNullOrWhiteSpace(suffix)) {
            return false;
        }

        return originUri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               originUri.Host.Length > suffix.Length;
    }

    // Hybrid local-dev override: dev-up.ps1 local olmayan servisler icin
    // `{SERVICE}__BASE_URL=https://dev-api.example.local` set ediyor. Bu helper
    // proxy target'i registry yerine env'den okur. Hangi servisin local oldugu
    // (`-Services notification-api`) launcher tarafinda kararlasiyor; orchestrator
    // sadece env var'i okuyup uygular. K8s/staging icin env yoksa no-op.
    // EndpointRegistryServiceLocator.ResolveFallbackBaseUrl ile ayni mantik.
    private static string? ResolveProxyTargetOverride(IConfiguration configuration, string? serviceName) {
        if (string.IsNullOrWhiteSpace(serviceName)) return null;
        var envKey = serviceName.Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
        var sectionKey = serviceName.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return configuration[$"{sectionKey}:BaseUrl"]
               ?? configuration[$"{sectionKey}:BASE_URL"]
               ?? configuration[$"{envKey}:BASE_URL"]
               ?? configuration[$"{envKey}:BaseUrl"]
               ?? configuration[$"{envKey}__BASE_URL"]
               ?? configuration[$"{envKey}__URL"];
    }
}

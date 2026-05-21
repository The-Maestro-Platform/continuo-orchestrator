using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Continuo.Shared.Context;

namespace Orchestrator.Hosting.Endpoints;

/// <summary>
/// Faz 3 — Cert-backed token mint endpoints.
///
/// Dev-api side:
///   POST /dev/sessions/cert-mint
///     Body: { certPem, signedChallengeBase64, challengeJson }
///     Verifies:
///       1. Cert chain valid (signed by our internal CA)
///       2. Cert not expired, not revoked (security-api lookup)
///       3. signedChallengeBase64 verifies signedChallengeBase64 over challengeJson with cert pub key
///       4. Challenge not replayed (nonce + exp check)
///       5. Requested services subset of cert's allowed_services
///     Returns: HMAC-signed dev-routing token (same shape as /dev/sessions HMAC path)
///
/// Local proxy side (also Orchestrator running locally):
///   POST /local/dev/cert-mint
///     Body: { developerId, services }
///     Reads dev cert + privKey from disk (~/.continuo/dev-cert.{pem,key} or DEV_CERT_PATH)
///     Builds challenge, signs with privKey, calls dev-api/dev/sessions/cert-mint
///     Returns: token to caller (toolbar)
///
/// SADECE Development environment'inda mapped.
/// </summary>
public sealed class DevCertMintEndpoints : TechEndpointBase {
    private static readonly TimeSpan ChallengeMaxAge = TimeSpan.FromSeconds(60);
    private static readonly HashSet<string> SeenNonces = new(StringComparer.Ordinal);
    private static readonly object NonceLock = new();
    private static DateTimeOffset _lastNonceClean = DateTimeOffset.UtcNow;

    // CA trust file mtime cache - re-read trust files when underlying file changes,
    // so security-api auto-bootstrapping a new CA after orch start doesn't require restart.
    private static readonly object CaTrustLock = new();
    private static List<X509Certificate2>? _cachedCaTrust;
    private static DateTime _cachedCaTrustMtime = DateTime.MinValue;
    private static string? _cachedCaTrustPath;

    public DevCertMintEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        var env = App.Services.GetRequiredService<IHostEnvironment>();
        if (!env.IsDevelopment()) return;

        var validator = App.Services.GetService<IDevRoutingTokenValidator>();
        var options = App.Services.GetService<DevRoutingOptions>();
        if (validator is null || options is null) {
            App.Logger.LogWarning("DevCertMintEndpoints: token validator missing - cert-mint disabled.");
            return;
        }

        // Dev-api side - CA trust loaded lazily on each request (with mtime cache)
        // so security-api auto-bootstrapping CA after orch start doesn't require restart.
        App.MapPost("/dev/sessions/cert-mint", async (CertMintRequest req, IHttpClientFactory httpFactory) => {
            var caTrust = LoadCaTrustChain(App.Configuration, App.Logger);
            if (req is null || string.IsNullOrWhiteSpace(req.CertPem) ||
                string.IsNullOrWhiteSpace(req.SignedChallengeBase64) ||
                string.IsNullOrWhiteSpace(req.ChallengeJson)) {
                return Results.BadRequest("certPem + signedChallengeBase64 + challengeJson required");
            }

            X509Certificate2 cert;
            try {
                cert = X509Certificate2.CreateFromPem(req.CertPem);
            }
            catch (Exception ex) {
                return Results.BadRequest($"Invalid cert PEM: {ex.Message}");
            }

            // 1) Cert chain valid - signed by our CA
            if (caTrust.Count == 0) {
                App.Logger.LogError("Dev CA trust chain not configured - cannot validate cert. Set DEV_CA_TRUST_PATH.");
                return Results.Problem("Server misconfigured: dev CA trust missing", statusCode: 500);
            }
            if (!IsChainValid(cert, caTrust, out var chainError)) {
                return Results.Json(new { error = "cert chain invalid", detail = chainError }, statusCode: 401);
            }

            // 2) Cert metadata + revocation check via security-api
            //    GET security-api/security/developers/certificates/{thumbprint}/public-key
            var securityApiBase = App.Configuration["SECURITY_API__BASE_URL"]
                ?? App.Configuration["security-api__BASE_URL"]
                ?? Environment.GetEnvironmentVariable("SECURITY_API__BASE_URL")
                ?? "http://localhost:5212";
            var http = httpFactory.CreateClient();
            http.BaseAddress = new Uri(securityApiBase.TrimEnd('/'));
            var lookupUrl = $"/security/developers/certificates/{cert.Thumbprint}/public-key";
            HttpResponseMessage lookupResp;
            try {
                lookupResp = await http.GetAsync(lookupUrl);
            }
            catch (Exception ex) {
                App.Logger.LogError(ex, "security-api cert lookup failed");
                return Results.Problem("security-api unreachable", statusCode: 503);
            }
            if (lookupResp.StatusCode == System.Net.HttpStatusCode.NotFound) {
                return Results.Json(new { error = "cert not registered" }, statusCode: 401);
            }
            if (lookupResp.StatusCode == System.Net.HttpStatusCode.Gone) {
                var body = await lookupResp.Content.ReadAsStringAsync();
                return Results.Json(new { error = "cert revoked or expired", detail = body }, statusCode: 401);
            }
            if (!lookupResp.IsSuccessStatusCode) {
                return Results.Problem($"cert lookup failed: {lookupResp.StatusCode}", statusCode: 502);
            }
            var lookup = await lookupResp.Content.ReadFromJsonAsync<CertLookupResponse>();
            if (lookup is null) {
                return Results.Problem("cert lookup empty", statusCode: 502);
            }

            // 3) Verify signedChallenge over challengeJson using cert public key
            var challengeBytes = Encoding.UTF8.GetBytes(req.ChallengeJson);
            byte[] sigBytes;
            try {
                sigBytes = Convert.FromBase64String(req.SignedChallengeBase64);
            }
            catch {
                return Results.BadRequest("signedChallengeBase64 invalid");
            }

            using var rsa = cert.GetRSAPublicKey();
            if (rsa is null) {
                return Results.BadRequest("cert pub key is not RSA");
            }
            var sigOk = rsa.VerifyData(challengeBytes, sigBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!sigOk) {
                App.Logger.LogWarning("Cert-mint signature mismatch for thumbprint {Thumb}", cert.Thumbprint);
                return Results.Json(new { error = "signature verification failed" }, statusCode: 401);
            }

            // 4) Parse challenge, validate nonce + freshness
            ChallengePayload? challenge;
            try {
                challenge = JsonSerializer.Deserialize<ChallengePayload>(req.ChallengeJson);
            }
            catch (Exception ex) {
                return Results.BadRequest($"challengeJson invalid: {ex.Message}");
            }
            if (challenge is null || string.IsNullOrWhiteSpace(challenge.nonce) ||
                string.IsNullOrWhiteSpace(challenge.devId)) {
                return Results.BadRequest("challenge missing nonce/devId");
            }
            var challengeIssuedAt = DateTimeOffset.FromUnixTimeSeconds(challenge.iat);
            if ((DateTimeOffset.UtcNow - challengeIssuedAt).Duration() > ChallengeMaxAge) {
                return Results.Json(new { error = "challenge stale or future-dated" }, statusCode: 401);
            }
            if (!RememberNonce(challenge.nonce)) {
                return Results.Json(new { error = "nonce replay" }, statusCode: 401);
            }

            // 5) DevId match (cert subject CN=dev:<id>)
            var certDevId = ExtractDeveloperIdFromSubject(cert.Subject);
            if (!string.Equals(certDevId, challenge.devId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(certDevId, lookup.developerId, StringComparison.OrdinalIgnoreCase)) {
                App.Logger.LogWarning(
                    "DevId mismatch: certCN={CertCN}, challenge={Challenge}, dbLookup={Db}",
                    certDevId, challenge.devId, lookup.developerId);
                return Results.Json(new { error = "developerId mismatch across cert/challenge/db" }, statusCode: 401);
            }

            // 6) Subset check: requested services subset of allowed_services
            var requested = challenge.services ?? Array.Empty<string>();
            var allowed = lookup.allowedServices ?? Array.Empty<string>();
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            var hasWildcard = allowedSet.Contains("*");
            if (!hasWildcard) {
                var rejected = requested.Where(r => !allowedSet.Contains(r)).ToArray();
                if (rejected.Length > 0) {
                    return Results.Json(new {
                        error = "services exceed cert authorization",
                        rejected,
                        allowed
                    }, statusCode: 403);
                }
            }

            // 7) Mint HMAC token (existing /dev/sessions logic, but bound to cert's allowed scope)
            var lifetime = options.TokenLifetime;
            var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
            var claims = new DevRoutingTokenClaims(
                DeveloperId: certDevId!,
                AuthorizedServices: requested,
                ExpiresAt: expiresAt
            );
            var token = validator.Issue(claims);

            App.Logger.LogInformation(
                "Cert-mint OK: dev={Dev} thumbprint={Thumb} services=[{Svc}] expires={Exp}",
                certDevId, cert.Thumbprint, string.Join(",", requested), expiresAt);

            return Results.Ok(new {
                token,
                expiresAt = expiresAt.ToUnixTimeSeconds(),
                authorizedServices = requested,
                certThumbprint = cert.Thumbprint
            });
        })
        .WithTags("DevRouting")
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy)
        .AllowAnonymous();

        // Local proxy: toolbar -> local orch -> dev-api
        App.MapPost("/local/dev/cert-mint", async (HttpContext httpCtx, LocalCertMintRequest req, IHttpClientFactory httpFactory) => {
            if (req is null || string.IsNullOrWhiteSpace(req.DeveloperId)) {
                return Results.BadRequest("developerId required");
            }
            // Read cert + privKey from disk
            var certPath = App.Configuration["DEV_CERT_PATH"]
                ?? Environment.GetEnvironmentVariable("DEV_CERT_PATH");
            var keyPath = App.Configuration["DEV_CERT_KEY_PATH"]
                ?? Environment.GetEnvironmentVariable("DEV_CERT_KEY_PATH");
            if (string.IsNullOrWhiteSpace(certPath) || string.IsNullOrWhiteSpace(keyPath)) {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                certPath ??= Path.Combine(home, ".continuo", "dev-cert.pem");
                keyPath ??= Path.Combine(home, ".continuo", "dev-cert.key");
            }
            if (!File.Exists(certPath) || !File.Exists(keyPath)) {
                return Results.Json(new {
                    error = "cert files missing",
                    cert = certPath,
                    key = keyPath,
                    hint = "Run scripts/dev/dev-routing-cert-bootstrap.ps1 to generate keypair, send CSR to admin"
                }, statusCode: 404);
            }

            X509Certificate2 cert;
            try {
                var certPem = await File.ReadAllTextAsync(certPath);
                var keyPem = await File.ReadAllTextAsync(keyPath);
                cert = X509Certificate2.CreateFromPem(certPem, keyPem);
            }
            catch (Exception ex) {
                return Results.BadRequest($"cert load failed: {ex.Message}");
            }

            var requested = req.Services ?? Array.Empty<string>();
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            var challenge = new ChallengePayload(
                nonce: nonce,
                devId: req.DeveloperId.Trim().ToLowerInvariant(),
                services: requested,
                iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );
            var challengeJson = JsonSerializer.Serialize(challenge);
            var challengeBytes = Encoding.UTF8.GetBytes(challengeJson);

            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is null) {
                return Results.BadRequest("cert key is not RSA");
            }
            var sigBytes = rsa.SignData(challengeBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var signedB64 = Convert.ToBase64String(sigBytes);

            // Call dev-api /dev/sessions/cert-mint
            // Default: SAME-ORIGIN (this orchestrator is its own dev-api in local Aspire setup).
            // Override DEV_ROUTING_DEV_API_URL env for hybrid scenarios where local proxy
            // forwards to a remote dev-api (e.g. dev-up.ps1 -Mode tunnel).
            var devApiBase = App.Configuration["DEV_ROUTING_DEV_API_URL"]
                ?? Environment.GetEnvironmentVariable("DEV_ROUTING_DEV_API_URL")
                ?? $"{httpCtx.Request.Scheme}://{httpCtx.Request.Host}";

            var http = httpFactory.CreateClient();
            http.BaseAddress = new Uri(devApiBase);
            HttpResponseMessage mintResp;
            try {
                mintResp = await http.PostAsJsonAsync("/dev/sessions/cert-mint", new CertMintRequest(
                    CertPem: PemEncoding.WriteString("CERTIFICATE", cert.RawData),
                    SignedChallengeBase64: signedB64,
                    ChallengeJson: challengeJson
                ));
            }
            catch (Exception ex) {
                return Results.Problem($"dev-api unreachable at {devApiBase}: {ex.Message}", statusCode: 502);
            }
            var body = await mintResp.Content.ReadAsStringAsync();
            return Results.Content(body, mintResp.Content.Headers.ContentType?.MediaType ?? "application/json",
                statusCode: (int)mintResp.StatusCode);
        })
        .WithTags("DevRouting")
        .RequireCors(OrchestratorHostingExtensions.GatewayCorsPolicy)
        .AllowAnonymous();

        App.Logger.LogInformation("DevCertMintEndpoints mapped (cert-mint + local proxy).");
    }

    private static List<X509Certificate2> LoadCaTrustChain(IConfiguration config, ILogger logger) {
        // DEV_CA_TRUST_PATH = file (single CA cert PEM) or directory of PEMs
        var path = config["DEV_CA_TRUST_PATH"] ?? Environment.GetEnvironmentVariable("DEV_CA_TRUST_PATH");
        if (string.IsNullOrWhiteSpace(path)) {
            // Default: ~/.continuo/dev-ca/ca.crt (security-api auto-creates this)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, ".continuo", "dev-ca", "ca.crt");
        }

        // mtime-based cache - reuse parsed trust list while underlying file unchanged.
        DateTime currentMtime;
        try {
            currentMtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path)
                : Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;
        }
        catch { currentMtime = DateTime.MinValue; }

        lock (CaTrustLock) {
            if (_cachedCaTrust is not null
                && _cachedCaTrustPath == path
                && _cachedCaTrustMtime == currentMtime
                && currentMtime != DateTime.MinValue) {
                return _cachedCaTrust;
            }

            var trust = new List<X509Certificate2>();
            try {
                if (Directory.Exists(path)) {
                    foreach (var file in Directory.EnumerateFiles(path, "*.crt").Concat(Directory.EnumerateFiles(path, "*.pem"))) {
                        trust.Add(X509Certificate2.CreateFromPem(File.ReadAllText(file)));
                    }
                }
                else if (File.Exists(path)) {
                    trust.Add(X509Certificate2.CreateFromPem(File.ReadAllText(path)));
                }
            }
            catch (Exception ex) {
                logger.LogError(ex, "Failed to load dev CA trust from {Path}", path);
            }
            if (trust.Count == 0) {
                logger.LogWarning("Dev CA trust empty - cert-mint will reject all requests until DEV_CA_TRUST_PATH is set or {Default} exists.", path);
            }
            else {
                logger.LogInformation("Dev CA trust (re)loaded: {Count} cert(s) from {Path}", trust.Count, path);
            }

            _cachedCaTrust = trust;
            _cachedCaTrustPath = path;
            _cachedCaTrustMtime = currentMtime;
            return trust;
        }
    }

    private static bool IsChainValid(X509Certificate2 cert, IList<X509Certificate2> trustedRoots, out string error) {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var root in trustedRoots) {
            chain.ChainPolicy.CustomTrustStore.Add(root);
        }
        var ok = chain.Build(cert);
        if (!ok) {
            error = string.Join("; ", chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool RememberNonce(string nonce) {
        lock (NonceLock) {
            // Periodic cleanup - drop set if older than 5 min (memory bound)
            if (DateTimeOffset.UtcNow - _lastNonceClean > TimeSpan.FromMinutes(5)) {
                SeenNonces.Clear();
                _lastNonceClean = DateTimeOffset.UtcNow;
            }
            return SeenNonces.Add(nonce);
        }
    }

    private static string? ExtractDeveloperIdFromSubject(string subject) {
        // Subject ornek: "CN=dev:mert, O=Continuo-DevRouting"
        var parts = subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts) {
            if (p.StartsWith("CN=dev:", StringComparison.OrdinalIgnoreCase)) {
                return p["CN=dev:".Length..].Trim().ToLowerInvariant();
            }
        }
        return null;
    }

    public sealed record CertMintRequest(string CertPem, string SignedChallengeBase64, string ChallengeJson);
    public sealed record LocalCertMintRequest(string DeveloperId, string[]? Services);
    public sealed record ChallengePayload(string nonce, string devId, string[]? services, long iat);
    public sealed record CertLookupResponse(string developerId, string thumbprint, string subject, string[] allowedServices, DateTime notBefore, DateTime notAfter);
}

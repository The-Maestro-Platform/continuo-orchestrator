using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Continuo.Configuration.Extensions;
using Continuo.Messaging;
using Continuo.Shared.Context;
using Orchestrator.Data;
using Orchestrator.Hosting.Endpoints;
using Orchestrator.Hosting.Middleware;
using Orchestrator.Services;
using Orchestrator.Services.Caching;
using Orchestrator.Services.Gateway;
using Orchestrator.Services.Identity;
using Orchestrator.Services.Import;
using Orchestrator.Services.Manifest;
using Orchestrator.Services.TenantConfig;
using Orchestrator.Services.Tenants;

namespace Orchestrator.Hosting;

public static class OrchestratorHostingExtensions {
    public const string ServiceName = "Orchestrator";
    public const string GatewayCorsPolicy = "GatewayCors";

    public static WebApplicationBuilder AddOrchestratorServices(this WebApplicationBuilder builder) {
        ConfigureDefaultUrl(builder);
        var authOptions = BuildAuthOptions(builder.Configuration);

        AddDatabase(builder);
        AddCaching(builder);
        AddCors(builder);
        AddAuth(builder, authOptions);
        AddCoreServices(builder, authOptions);
        builder.Services.Configure<SecurityHeadersOptions>(
            builder.Configuration.GetSection(SecurityHeadersOptions.SectionName));
        builder.Services.Configure<ProxyRequestXssOptions>(
            builder.Configuration.GetSection(ProxyRequestXssOptions.SectionName));
        builder.Services.AddControllers();

        // Dev-routing token validator (HMAC-signed). SADECE Development env'de aktif;
        // helper extension içinde IsDevelopment guard'ı var. Production'da no-op.
        builder.Services.AddDevRouting(builder.Configuration, builder.Environment);

        return builder;
    }

    public static WebApplication UseOrchestratorPipeline(this WebApplication app) {
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseWebSockets();
        app.UseCors(GatewayCorsPolicy);
        // NOT: cookie→Bearer middleware ve UseAuthentication/UseAuthorization artık
        // Bootstrap.CreateApp (configureBeforeAuth callback) tarafından register ediliyor.
        // Buradan çıkarıldı çünkü Bootstrap'in UseAuth'ı önce çağrılıyordu, dolayısıyla
        // RequireAuthorization() taşıyan endpoint'lerde cookie hiç okunamıyordu.
        // TenantResolutionMiddleware runs after auth so it can read tenant_id from JWT claims.
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.MapControllers();

        new DiagnosticsEndpoints(app).Map();
        new SecurityEndpoints(app).Map();
        new AdminEndpoints(app).Map();
        new CatalogEndpoints(app).Map();
        new DevSessionsEndpoints(app).Map();
        new DevCertMintEndpoints(app).Map();
        new UiManifestEndpoints(app).Map();
        new ProxyEndpoints(app).Map();

        return app;
    }

    public static async Task PrepareOrchestratorAsync(this WebApplication app) {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OrchestratorStartup");
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Hybrid local-dev guard: laptop'taki orchestrator shared dev DB'ye
        // baglandiginda istemeden migration veya manifest write yapip dev'i
        // bozabilir. `ORCH__SKIP_DB_MIGRATIONS=true` set'liyse (dev-up.ps1
        // tarafindan local launch'ta otomatik) migration yok; bu pending'leri
        // de logla ki "neden eski schema?" karistirmasin.
        //
        // Config key resolution: env var `ORCH__SKIP_DB_MIGRATIONS` .NET
        // env provider tarafindan `ORCH:SKIP_DB_MIGRATIONS` key'ine map edilir.
        // Hem `:` key'ini hem direct env read'i dene — biri kesin yakalar
        // (bazi host platformlarinda __→: mapping kacirilabiliyor).
        var skipMigrationsRaw = config["ORCH:SKIP_DB_MIGRATIONS"]
                                ?? config["ORCH__SKIP_DB_MIGRATIONS"]
                                ?? Environment.GetEnvironmentVariable("ORCH__SKIP_DB_MIGRATIONS");
        var skipMigrations = string.Equals(skipMigrationsRaw, "true", StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.OrdinalIgnoreCase)) {
            var pending = await db.Database.GetPendingMigrationsAsync();
            if (pending.Any()) {
                if (skipMigrations) {
                    logger.LogWarning(
                        "ORCH__SKIP_DB_MIGRATIONS=true → skipping {Count} pending migration(s) on shared DB: {Migrations}. " +
                        "Schema drift olursa local kodun beklenenden eski/yeni DB'ye carpabilir.",
                        pending.Count(), string.Join(",", pending));
                }
                else {
                    try {
                        await db.Database.MigrateAsync();
                    }
                    catch (SqlException ex) when (ex.Number == 2714) // object already exists
                    {
                        var reconciled = await EnsureSqlServerHistoryAsync(db);
                        if (!reconciled) {
                            throw;
                        }

                        pending = await db.Database.GetPendingMigrationsAsync();
                        if (pending.Any()) {
                            await db.Database.MigrateAsync();
                        }
                    }
                }
            }
        }
        if (!skipMigrations) {
            await UiAppSeeder.SeedAsync(db);
        }
        else {
            logger.LogInformation("ORCH__SKIP_DB_MIGRATIONS=true → UiApp seeder atlandi (shared DB read-only).");
        }
        var manifestSync = scope.ServiceProvider.GetRequiredService<ManifestSyncService>();
        try {
            // Best-effort short sync so the registry is warm for the first request when
            // possible. Bounded to 20s — if SQL is slow during Aspire cold-start, we skip
            // and let the ManifestSyncService background retry loop finish the job.
            //
            // force=false: SyncCoreAsync now hashes manifest content + per-service
            // resolved URLs (env-var driven). If nothing actually changed since the
            // last sync, the call is a single-row SELECT against orch.Meta and exits
            // without opening a transaction.
            var synced = await manifestSync.TrySyncAsync(
                force: false,
                waitTimeout: TimeSpan.FromSeconds(5),
                operationTimeout: TimeSpan.FromSeconds(20));
            if (!synced) {
                logger.LogInformation("Startup manifest sync skipped (lock unavailable). Background retry loop will handle it.");
            }
        }
        catch (Exception ex) {
            logger.LogWarning(ex, "Startup manifest sync failed. Background retry loop will keep trying; registry serves previously-loaded routes in the meantime.");
        }
        var uiRegistry = scope.ServiceProvider.GetRequiredService<UiAppRegistry>();
        try {
            await uiRegistry.ReloadAsync();
        }
        catch (Exception ex) {
            logger.LogWarning(ex, "UiAppRegistry reload failed at startup. Continuing; it will be refreshed by subsequent manifest syncs.");
        }
    }

    private static void ConfigureDefaultUrl(WebApplicationBuilder builder) {
        var urlsFromEnv = builder.Configuration["ASPNETCORE_URLS"];
        if (string.IsNullOrWhiteSpace(urlsFromEnv)) {
            builder.WebHost.UseUrls("http://0.0.0.0:4000");
        }
    }

    private static void AddDatabase(WebApplicationBuilder builder) {
        // Connection string resolution priority (env vars normalised by .NET Configuration:
        // `ORCHESTRATOR_DB__CONN` env → `ORCHESTRATOR_DB:CONN` config key. We must read the
        // colon form because `Configuration["ORCHESTRATOR_DB__CONN"]` returns null for a
        // double-underscore env var. Previously a hardcoded dev-DB fallback hid this bug:
        // staging would silently connect to dev's `continuo` DB and corrupt cross-env state.
        // Now: try every form, then read raw env, then fail-fast.
        var rawConnectionString =
            builder.Configuration.GetConnectionString("OrchestratorDb")
            ?? builder.Configuration["ORCHESTRATOR_DB:CONN"]
            ?? builder.Configuration["ORCHESTRATOR_DB__CONN"]
            ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_DB__CONN")
            ?? throw new InvalidOperationException(
                "Orchestrator DB connection string not configured. Set one of: " +
                "ConnectionStrings__OrchestratorDb, ORCHESTRATOR_DB__CONN.");

        // Apply platform-wide connect-timeout + pooling defaults so a slow on-prem
        // SQL Server post-login phase does not kill startup at the framework's
        // default 15s pre-login budget. Same helper the persistence building-block
        // uses for every other service's DbContext.
        var connectionString = Continuo.Persistence.PersistenceExtensions
            .ApplyConnectionStringDefaults(rawConnectionString, builder.Configuration);
        var commandTimeoutSeconds = ResolveCommandTimeoutSeconds(builder.Configuration);

        var historySchema = Environment.GetEnvironmentVariable("ORCH__SCHEMA") ?? "orch";
        builder.Services.AddDbContext<OrchestratorDbContext>(options => {
            options.UseSqlServer(connectionString, sqlOptions => {
                sqlOptions.EnableRetryOnFailure();
                sqlOptions.CommandTimeout(commandTimeoutSeconds);
                // EF defaults the history table to dbo.__EFMigrationsHistory regardless
                // of HasDefaultSchema. Without this every restart re-runs InitialCreate
                // and 2714's against the existing orch tables. The schema-aware self-heal
                // (EnsureSqlServerHistoryAsync) writes to orch.* which EF never reads.
                Continuo.Persistence.SchemaTools.ConfigureHistory(sqlOptions, historySchema);
            });
        });
    }

    private static int ResolveCommandTimeoutSeconds(IConfiguration config) {
        var raw =
            config["Persistence:CommandTimeoutSeconds"]
            ?? config["PERSISTENCE__COMMAND_TIMEOUT_SECONDS"]
            ?? config["CONTINUO__PERSISTENCE__COMMAND_TIMEOUT_SECONDS"];
        if (int.TryParse(raw, out var seconds) && seconds > 0) {
            return seconds;
        }
        return 90;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "EF1002:Risk of vulnerability to SQL injection.", Justification = "Schema name is from EF model metadata (compile-time), not user input.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Schema name is from EF model metadata (compile-time), not user input.")]
    private static async Task<bool> EnsureSqlServerHistoryAsync(OrchestratorDbContext db) {
        try {
            // Schema-aware history: each context owns its own __EFMigrationsHistory table
            // INSIDE its default schema (e.g. orch.__EFMigrationsHistory). Without the schema
            // prefix, the IF-NOT-EXISTS check matches OTHER services' history tables (in their
            // own schemas — aut, sec, ord, …) and we silently skip table creation, then the
            // unprefixed INSERT fails because there's no history table in dbo. The result was:
            // EF thinks all migrations are applied even though the orch schema is empty,
            // tenant resolution / proxy routing all 503/404 forever.
            var schema = db.Model.GetDefaultSchema() ?? "dbo";
            var migrations = db.GetService<IMigrationsAssembly>();

            // Create history table in the right schema if missing.
            await db.Database.ExecuteSqlRawAsync($@"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{schema}')
    EXEC(N'CREATE SCHEMA [{schema}]');
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = N'__EFMigrationsHistory' AND s.name = N'{schema}')
BEGIN
    CREATE TABLE [{schema}].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory_{schema}] PRIMARY KEY ([MigrationId])
    );
END");

            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = db.Database.GetDbConnection().CreateCommand()) {
                command.CommandText = $"SELECT [MigrationId] FROM [{schema}].[__EFMigrationsHistory]";
                if (command.Connection!.State != System.Data.ConnectionState.Open) {
                    await command.Connection.OpenAsync();
                }
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    applied.Add(reader.GetString(0));
                }
            }

            foreach (var migrationId in migrations.Migrations.Keys) {
                if (applied.Contains(migrationId)) {
                    continue;
                }

                var entry = migrations.Migrations[migrationId];
                var productVersion = entry?.Assembly?.GetName().Version?.ToString()
                    ?? typeof(OrchestratorDbContext).Assembly.GetName().Version?.ToString()
                    ?? "0.0.0";

                // schema is from validated set (Model.GetDefaultSchema()); safe to interpolate identifier.
                await db.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO [{schema}].[__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (@p0, @p1)",
                    migrationId, productVersion);
            }

            return true;
        }
        catch {
            return false;
        }
    }

    private static void AddCaching(WebApplicationBuilder builder) {
        builder.Services.AddMemoryCache();
        var cacheSection = builder.Configuration.GetSection("Cache");
        builder.Services.Configure<CacheSettings>(cacheSection);

        var provider = cacheSection["Provider"] ?? "Redis";
        var redisHost = Environment.GetEnvironmentVariable("REDIS__HOST")
                       ?? builder.Configuration["REDIS:HOST"]
                       ?? builder.Configuration["REDIS__HOST"];
        var redisPort = Environment.GetEnvironmentVariable("REDIS__PORT")
                       ?? builder.Configuration["REDIS:PORT"]
                       ?? builder.Configuration["REDIS__PORT"];
        var redisConn = cacheSection.GetValue<string>("Redis:ConnectionString")
                        ?? Environment.GetEnvironmentVariable("REDIS__CONNECTION")
                        ?? Environment.GetEnvironmentVariable("REDIS__CONN")
                        ?? builder.Configuration["REDIS:CONNECTION"]
                        ?? builder.Configuration["REDIS:CONN"]
                        ?? builder.Configuration["REDIS__CONNECTION"]
                        ?? builder.Configuration["REDIS__CONN"];

        if (string.IsNullOrWhiteSpace(redisConn) && !string.IsNullOrWhiteSpace(redisHost)) {
            redisConn = string.IsNullOrWhiteSpace(redisPort) ? redisHost : $"{redisHost}:{redisPort}";
        }

        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(redisConn)) {
            builder.Services.AddSingleton<ICacheProvider, InMemoryCacheProvider>();
            builder.Services.AddDataProtection();
        }
        else {
            var options = ConfigurationOptions.Parse(redisConn);
            if (IsAllowUntrustedRedisCert(builder.Configuration)) {
#pragma warning disable CA5359 // Development-only: allow untrusted redis certs when explicitly enabled by config.
                options.CertificateValidation += (_, _, _, _) => true;
#pragma warning restore CA5359
            }
            var multiplexer = ConnectionMultiplexer.Connect(options);
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ => multiplexer);
            builder.Services.AddSingleton<ICacheProvider, RedisCacheProvider>();
            builder.Services.AddDataProtection()
                .PersistKeysToStackExchangeRedis(multiplexer, "Orchestrator-DataProtection");
        }
    }

    private static void AddCoreServices(WebApplicationBuilder builder, OrchestratorAuthOptions authOptions) {
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton<EndpointRegistry>();
        builder.Services.AddSingleton<UiAppRegistry>();
        builder.Services.AddSingleton<IServiceLocator, EndpointRegistryServiceLocator>();
        builder.Services.AddSingleton<ICallStackStore, InMemoryCallStackStore>();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddScoped<TenantDirectoryClient>();
        builder.Services.AddScoped<ServiceCallExecutor>();
        builder.Services.AddScoped<MultipartServiceCallExecutor>();
        builder.Services.AddScoped<GatewayInvocationService>();
        builder.Services.AddScoped<TenantConfigService>();
        builder.Services.AddSingleton<SwaggerImportService>();
        builder.Services.AddHttpClient("proxy");
        // 2026-05-18: ORCH__ADMIN__TOKEN'i security-api'den almak icin
        // (AdminAccessFilter env fallback'i null oldugunda). M2MKey resolver'in
        // dependency'si — security-api'ye M2M auth header'i icin gerekli.
        builder.Services.AddPlatformM2MKey();
        builder.Services.AddPlatformSecretResolver();
        builder.Services.AddSingleton<ManifestSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ManifestSyncService>());
        builder.Services.AddHostedService<Orchestrator.Services.Parameters.ParameterCacheWarmupService>();
        builder.Services.AddEndpointsApiExplorer();

        builder.Services.AddMassTransit(x => {
            x.AddConsumer<GatewayInvokeCommandConsumer>();
            x.ConfigureRabbitMq(builder.Configuration, ServiceName);
        });
    }

    private static bool IsAllowUntrustedRedisCert(IConfiguration configuration) {
        var raw = Environment.GetEnvironmentVariable("REDIS__ALLOW_UNTRUSTED_CERT")
                  ?? Environment.GetEnvironmentVariable("REDIS_ALLOW_UNTRUSTED_CERT")
                  ?? configuration["REDIS:ALLOW_UNTRUSTED_CERT"]
                  ?? configuration["REDIS__ALLOW_UNTRUSTED_CERT"];
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCors(WebApplicationBuilder builder) {
        builder.Services.AddCors(options => {
            options.AddPolicy(GatewayCorsPolicy, policy => {
                policy.SetIsOriginAllowed(origin => {
                    if (string.IsNullOrWhiteSpace(origin)) {
                        return false;
                    }

                    if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)) {
                        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("127.0.0.1")) {
                            return true;
                        }
                    }
                    var allowed = builder.Configuration["CORS:ORIGINS"] ?? builder.Configuration["CORS__ORIGINS"];
                    if (string.IsNullOrWhiteSpace(allowed)) {
                        return false;
                    }

                    var entries = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return entries.Any(o => IsOriginMatch(o, origin));
                })
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
            });
        });
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

        // Support simple wildcard hosts (e.g. https://*.example.local).
        // Must include scheme; wildcard only allowed as the left-most label prefix "*.".
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) {
            return false;
        }

        if (!Uri.TryCreate(allowed.Replace("*.", "wildcard."), UriKind.Absolute, out var allowedUri)) {
            return false;
        }

        if (!string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // If allowed explicitly specifies a non-default port, require exact match.
        if (!allowedUri.IsDefaultPort && originUri.Port != allowedUri.Port) {
            return false;
        }

        var allowedHost = allowedUri.Host;
        if (!allowedHost.StartsWith("wildcard.", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var suffix = allowedHost["wildcard.".Length..];
        return originUri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               originUri.Host.Length > suffix.Length;
    }

    private static void AddAuth(WebApplicationBuilder builder, OrchestratorAuthOptions authOptions) {
        if (!authOptions.Enabled || string.IsNullOrWhiteSpace(authOptions.Secret)) {
            return;
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.Secret));
        builder.Services.AddAuthentication(options => {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(options => {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters {
                ValidateIssuer = !string.IsNullOrWhiteSpace(authOptions.Issuer),
                ValidateAudience = !string.IsNullOrWhiteSpace(authOptions.Audience),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidIssuer = authOptions.Issuer,
                ValidAudience = authOptions.Audience,
                ValidateLifetime = true
            };
        });

        builder.Services.AddAuthorization(options => {
            options.AddPolicy(PlatformPolicies.RequireDevOps, policy => {
                policy.RequireAssertion(ctx => ctx.User.HasAnyRole(PlatformRoles.DevOps));
            });

            options.AddPolicy(PlatformPolicies.RequireDevOrAnalyst, policy => {
                policy.RequireAssertion(ctx => ctx.User.HasAnyRole(PlatformRoles.Developer, PlatformRoles.Analyst));
            });

            options.AddPolicy(PlatformPolicies.RequireDevOpsOrDeveloperOrAnalyst, policy => {
                policy.RequireAssertion(ctx => ctx.User.HasAnyRole(PlatformRoles.DevOps, PlatformRoles.Developer, PlatformRoles.Analyst));
            });

        });
    }

    private static OrchestratorAuthOptions BuildAuthOptions(IConfiguration configuration) {
        var jwtIssuer = configuration["ORCH:JWT:ISSUER"] ?? configuration["ORCH__JWT__ISSUER"] ?? configuration["JWT:ISSUER"] ?? configuration["JWT__ISSUER"];
        var jwtAudience = configuration["ORCH:JWT:AUDIENCE"] ?? configuration["ORCH__JWT__AUDIENCE"] ?? configuration["JWT:AUDIENCE"] ?? configuration["JWT__AUDIENCE"];
        var jwtSecret = configuration["ORCH:JWT:SECRET"] ?? configuration["ORCH__JWT__SECRET"] ?? configuration["JWT:SECRET"] ?? configuration["JWT__SECRET"];
        if (string.IsNullOrWhiteSpace(jwtSecret)) {
            jwtSecret = "dev-gateway-secret-CHANGE-ME";
        }

        return new OrchestratorAuthOptions {
            Enabled = true,
            Issuer = jwtIssuer,
            Audience = jwtAudience,
            Secret = jwtSecret
        };
    }

}

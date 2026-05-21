using Continuo.Configuration.Extensions;
using Continuo.Observability;
using Orchestrator.Hosting;
using Orchestrator.Hosting.Endpoints;

const string serviceName = "Orchestrator";

var builder = Bootstrap.CreateBuilder(args, serviceName);
builder.AddOrchestratorServices();

// Cookie→Bearer translation Bootstrap'in UseAuthentication'ından ÖNCE register
// olmalı. Yoksa RequireAuthorization() taşıyan endpoint'ler (catalog/services, admin/*)
// AuthorizationMiddleware tarafından 401'le reddedilir — cookie middleware çalışmaya
// fırsat bulamaz. 2026-05-19 /catalog/services 401 incident bu sıralama bozukluğuydu.
var app = Bootstrap.CreateApp(builder, serviceName, configureBeforeAuth: a => {
    a.Use((context, next) => {
        if (!TechEndpointBase.ShouldSkipAuthForPath(context.Request.Path) &&
            !context.Request.Headers.ContainsKey("Authorization") &&
            AuthCookieResolver.TryResolveAuthToken(
                context,
                uiDescriptor: null,
                clientApp: context.Request.Headers["X-Client-App"].FirstOrDefault(),
                token: out var authToken) &&
            !string.IsNullOrWhiteSpace(authToken)) {
            context.Request.Headers["Authorization"] = $"Bearer {authToken}";
        }

        return next();
    });
});
app.UseExceptionLogging();
app.UseRequestResponseLogging();
await app.PrepareOrchestratorAsync();
app.UseOrchestratorPipeline();

app.Run();

public partial class Program { }

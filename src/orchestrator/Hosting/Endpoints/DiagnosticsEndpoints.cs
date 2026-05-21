namespace Orchestrator.Hosting.Endpoints;

public sealed class DiagnosticsEndpoints : TechEndpointBase {
    public DiagnosticsEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        // Default path for load balancer health checks. Must be fast and not depend on tenant resolution.
        App.MapGet("/", () => Results.Ok(new { Service = OrchestratorHostingExtensions.ServiceName, Status = "Healthy" }))
           .WithTags("Diagnostics");

        App.MapGet("/orchestrator/health", () => Results.Ok(new { Service = OrchestratorHostingExtensions.ServiceName, Status = "Healthy" }))
           .WithTags("Diagnostics");
    }
}

using Microsoft.EntityFrameworkCore;
using Orchestrator.Data;
using Orchestrator.Services;

namespace Orchestrator.Hosting.Endpoints;

public sealed class CatalogEndpoints : TechEndpointBase {
    public CatalogEndpoints(WebApplication app) : base(app) { }

    public override void Map() {
        var group = MapCatalogGroup();

        group.MapGet("/services", async (OrchestratorDbContext db, IConfiguration configuration, CancellationToken ct) => {
            var mode = ServiceUrlSelector.GetMode(configuration);
            var services = await db.Services
                .OrderBy(x => x.Name)
                .Select(x => new {
                    x.Id,
                    x.Name,
                    x.BaseUrl,
                    x.InternalBaseUrl,
                    x.ExternalBaseUrl,
                    EffectiveBaseUrl = ServiceUrlSelector.Resolve(x, mode),
                    x.Version
                })
                .ToListAsync(ct);

            return Results.Ok(services);
        });

        group.MapGet("/services/{serviceId:guid}/endpoints", async (Guid serviceId, OrchestratorDbContext db, CancellationToken ct) => {
            var endpoints = await db.Endpoints
                .Where(x => x.ServiceId == serviceId && x.Enabled)
                .OrderBy(x => x.Path)
                .ThenBy(x => x.Method)
                .Select(x => new {
                    x.Id,
                    x.ServiceId,
                    x.Path,
                    x.Method,
                    x.OperationId
                })
                .ToListAsync(ct);

            return Results.Ok(endpoints);
        });
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Continuo.Persistence;

namespace Orchestrator.Data;

public class OrchestratorDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext> {
    public OrchestratorDbContext CreateDbContext(string[] args) {
        var optionsBuilder = new DbContextOptionsBuilder<OrchestratorDbContext>();
        var connectionString =
            Environment.GetEnvironmentVariable("ORCHESTRATOR_DB__CONN") ??
            Environment.GetEnvironmentVariable("ORCHESTRATOR__DB__CONN") ??
            "Server=94.73.170.39;Database=continuo;User Id=continuo;Password=T9glh-wlA8MufkGm;TrustServerCertificate=True;";

        var schema = Environment.GetEnvironmentVariable("ORCH__SCHEMA") ?? "orch";
        optionsBuilder.UseSqlServer(connectionString, sql => SchemaTools.ConfigureHistory(sql, schema));
        return new OrchestratorDbContext(optionsBuilder.Options);
    }
}

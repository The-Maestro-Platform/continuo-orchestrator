using Microsoft.EntityFrameworkCore;
using Orchestrator.Models;

namespace Orchestrator.Data;

public class OrchestratorDbContext : DbContext {
    private static readonly string Schema = Environment.GetEnvironmentVariable("ORCH__SCHEMA") ?? "orch";

    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : base(options) { }

    public DbSet<UiApp> UiApps => Set<UiApp>();
    public DbSet<ServiceEntry> Services => Set<ServiceEntry>();
    public DbSet<EndpointEntry> Endpoints => Set<EndpointEntry>();
    public DbSet<UiAppEndpoint> UiAppEndpoints => Set<UiAppEndpoint>();

    // Override tables
    public DbSet<UiAppOverride> UiAppOverrides => Set<UiAppOverride>();
    public DbSet<ServiceOverride> ServiceOverrides => Set<ServiceOverride>();
    public DbSet<EndpointOverride> EndpointOverrides => Set<EndpointOverride>();

    public DbSet<OrchestratorMeta> Meta => Set<OrchestratorMeta>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        if (!string.IsNullOrWhiteSpace(Schema)) {
            modelBuilder.HasDefaultSchema(Schema);
        }

        modelBuilder.Entity<UiApp>(b => {
            b.ToTable("UiApps", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.ClientKey).HasMaxLength(256);
            b.Property(x => x.AllowedOriginsJson).HasMaxLength(2000);
        });

        modelBuilder.Entity<ServiceEntry>(b => {
            b.ToTable("Services", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.BaseUrl).HasMaxLength(512).IsRequired();
            b.Property(x => x.InternalBaseUrl).HasMaxLength(512);
            b.Property(x => x.ExternalBaseUrl).HasMaxLength(512);
            b.Property(x => x.Version).HasMaxLength(32);
        });

        modelBuilder.Entity<EndpointEntry>(b => {
            b.ToTable("Endpoints", Schema);
            b.HasKey(x => x.Id);
            b.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId);
            b.Property(x => x.Path).HasMaxLength(400).IsRequired();
            b.Property(x => x.Method).HasMaxLength(16).IsRequired();
            b.Property(x => x.OperationId).HasMaxLength(200);
            b.Property(x => x.TagsJson).HasMaxLength(2000);
            b.Property(x => x.RolesJson).HasMaxLength(2000);
            b.Property(x => x.PoliciesJson).HasMaxLength(2000);
            b.Property(x => x.CacheStrategy).HasMaxLength(64);
        });

        modelBuilder.Entity<UiAppEndpoint>(b => {
            b.ToTable("UiAppEndpoints", Schema);
            b.HasKey(x => x.Id);
            b.HasOne(x => x.UiApp).WithMany().HasForeignKey(x => x.UiAppId);
            b.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointId);
            b.HasIndex(x => new { x.UiAppId, x.EndpointId }).IsUnique();
        });

        // Override tables configuration
        modelBuilder.Entity<UiAppOverride>(b => {
            b.ToTable("UiApps_Override", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.ClientKey).HasMaxLength(256);
            b.Property(x => x.AllowedOriginsJson).HasMaxLength(2000);
            b.HasIndex(x => x.OriginalId);
        });

        modelBuilder.Entity<ServiceOverride>(b => {
            b.ToTable("Services_Override", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.BaseUrl).HasMaxLength(512).IsRequired();
            b.Property(x => x.InternalBaseUrl).HasMaxLength(512);
            b.Property(x => x.ExternalBaseUrl).HasMaxLength(512);
            b.Property(x => x.Version).HasMaxLength(32);
            b.HasIndex(x => x.OriginalId);
        });

        modelBuilder.Entity<EndpointOverride>(b => {
            b.ToTable("Endpoints_Override", Schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Path).HasMaxLength(400).IsRequired();
            b.Property(x => x.Method).HasMaxLength(16).IsRequired();
            b.Property(x => x.OperationId).HasMaxLength(200);
            b.Property(x => x.TagsJson).HasMaxLength(2000);
            b.Property(x => x.RolesJson).HasMaxLength(2000);
            b.Property(x => x.PoliciesJson).HasMaxLength(2000);
            b.Property(x => x.CacheStrategy).HasMaxLength(64);
            b.HasIndex(x => x.OriginalId);
        });

        modelBuilder.Entity<OrchestratorMeta>(b => {
            b.ToTable("Meta", Schema);
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(128);
            b.Property(x => x.Value).HasMaxLength(2000);
        });

    }

    private bool IsPostgres => Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}

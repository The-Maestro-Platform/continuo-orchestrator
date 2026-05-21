namespace Orchestrator.Services.Caching;

public class CacheSettings {
    public string Provider { get; set; } = "Redis";
    public RedisSettings Redis { get; set; } = new();

    public TenantConfigSettings TenantConfig { get; set; } = new();
}

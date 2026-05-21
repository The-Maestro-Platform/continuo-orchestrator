namespace Orchestrator.Services.Tenants;

public class TenantContext : ITenantContext {
    private TenantInfo? _tenant;

    public Guid? TenantId => _tenant?.Id;

    public string TenantSlug => _tenant?.Slug ?? string.Empty;

    public TenantInfo? Tenant => _tenant;

    public void SetTenant(TenantInfo tenant) {
        _tenant = tenant;
    }
}

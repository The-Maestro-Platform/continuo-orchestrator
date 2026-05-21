namespace Orchestrator.Services.Tenants;

public interface ITenantContext {
    Guid? TenantId { get; }
    string TenantSlug { get; }
    TenantInfo? Tenant { get; }
    bool HasTenant => TenantId.HasValue;
    void SetTenant(TenantInfo tenant);
}

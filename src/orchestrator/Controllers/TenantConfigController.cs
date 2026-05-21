using Microsoft.AspNetCore.Mvc;
using Orchestrator.Services.TenantConfig;
using Orchestrator.Services.Tenants;
using TenantConfigDto = Orchestrator.Application.TenantConfig.TenantConfigDto;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/tenant")]
public class TenantConfigController : ControllerBase {
    private readonly TenantConfigService _tenantConfigService;
    private readonly ITenantContext _tenantContext;

    public TenantConfigController(
        TenantConfigService tenantConfigService,
        ITenantContext tenantContext) {
        _tenantConfigService = tenantConfigService;
        _tenantContext = tenantContext;
    }

    [HttpGet("config")]
    public async Task<ActionResult<TenantConfigDto>> GetTenantConfig(CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(_tenantContext.TenantSlug)) {
            return BadRequest("Tenant context has not been initialized.");
        }

        try {
            var config = await _tenantConfigService.GetConfigAsync(ct);
            return Ok(config);
        }
        catch (InvalidOperationException ex) {
            return NotFound(ex.Message);
        }
    }
}

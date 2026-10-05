using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Multitenancy.Services;

public interface IEventTenantResolver
{
    Task<AppTenantInfo?> ResolveAsync(string tenantId, CancellationToken ct = default);
}
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Multitenancy.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Multitenancy.Services;

public sealed class EventTenantResolver(TenantDbContext context) : IEventTenantResolver
{
    public Task<AppTenantInfo?> ResolveAsync(string tenantId, CancellationToken ct = default)
        => context.Set<AppTenantInfo>().AsNoTracking()
            .SingleOrDefaultAsync(tenant => tenant.Id == tenantId, ct);
}
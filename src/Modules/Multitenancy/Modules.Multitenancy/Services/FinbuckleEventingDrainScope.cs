using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Multitenancy.Services;

/// <summary>
/// Installs an <see cref="AppTenantInfo"/> carrying the tenant's connection string, so an
/// <c>EventingDbContext</c> built inside the scope routes to that tenant's database.
///
/// Distinct from <see cref="FinbuckleEventTenantScope"/>, which resolves the event's consumer
/// tenant. This scope selects the source database for a drain pass, independently of the consumer.
/// </summary>
public sealed class FinbuckleEventingDrainScope : IEventingDrainScope
{
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _accessor;
    private readonly IMultiTenantContextSetter _setter;

    public FinbuckleEventingDrainScope(
        IMultiTenantContextAccessor<AppTenantInfo> accessor,
        IMultiTenantContextSetter setter)
    {
        _accessor = accessor;
        _setter = setter;
    }

    public IDisposable Begin(EventingDrainTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var previous = _accessor.MultiTenantContext;

        // Built by hand rather than via the tenant-shaped constructor: only the id and the
        // connection string matter for routing, and the richer constructor also stamps validity
        // and activation state we have no business inventing here.
        string tenantId = target.TenantId ?? MultitenancyConstants.Root.Id;
        var info = new AppTenantInfo(tenantId, tenantId)
        {
            ConnectionString = target.ConnectionString ?? string.Empty,
        };

        _setter.MultiTenantContext = new MultiTenantContext<AppTenantInfo>(info);

        return new RestoreScope(_setter, previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly IMultiTenantContextSetter _setter;
        private readonly IMultiTenantContext<AppTenantInfo> _previous;

        public RestoreScope(IMultiTenantContextSetter setter, IMultiTenantContext<AppTenantInfo> previous)
        {
            _setter = setter;
            _previous = previous;
        }

        public void Dispose() => _setter.MultiTenantContext = _previous;
    }

}

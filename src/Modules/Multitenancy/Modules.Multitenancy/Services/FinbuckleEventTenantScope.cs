using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Multitenancy.Services;

/// <summary>
/// Finbuckle-backed <see cref="IEventTenantScope"/>. Installs the ambient tenant context
/// (an AsyncLocal in Finbuckle) for the duration of an integration-event dispatch so that
/// handler DbContexts resolved afterwards capture a real <c>TenantInfo</c> instead of the
/// null one a background scope would otherwise carry.
///
/// Async dispatch resolves the full tenant from the default catalog before constructing
/// consumers. The callback keeps that context in the same asynchronous execution flow.
/// </summary>
public sealed class FinbuckleEventTenantScope : IEventTenantScope
{
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _accessor;
    private readonly IMultiTenantContextSetter _setter;
    private readonly IServiceScopeFactory? _scopeFactory;

    public FinbuckleEventTenantScope(
        IMultiTenantContextAccessor<AppTenantInfo> accessor,
        IMultiTenantContextSetter setter)
    {
        _accessor = accessor;
        _setter = setter;
    }

    public FinbuckleEventTenantScope(
        IMultiTenantContextAccessor<AppTenantInfo> accessor,
        IMultiTenantContextSetter setter,
        IServiceScopeFactory scopeFactory)
        : this(accessor, setter)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    public IDisposable Begin(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // Global events: leave the ambient context untouched.
            return NoopScope.Instance;
        }

        var previous = _accessor.MultiTenantContext;
        if (string.Equals(previous.TenantInfo?.Id, tenantId, StringComparison.Ordinal))
        {
            return new RestoreScope(_setter, previous);
        }

        _setter.MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(new AppTenantInfo(tenantId, tenantId));

        return new RestoreScope(_setter, previous);
    }

    public async Task ExecuteAsync(
        string? tenantId,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();
        var previous = _accessor.MultiTenantContext;

        try
        {
            var root = new AppTenantInfo(MultitenancyConstants.Root.Id, MultitenancyConstants.Root.Id)
            {
                IsActive = true
            };
            _setter.MultiTenantContext = new MultiTenantContext<AppTenantInfo>(root);

            AppTenantInfo tenant = root;
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                if (_scopeFactory is null)
                {
                    throw new InvalidOperationException("Async event dispatch requires a tenant catalog scope.");
                }

                await using var scope = _scopeFactory.CreateAsyncScope();
                tenant = await scope.ServiceProvider.GetRequiredService<IEventTenantResolver>()
                    .ResolveAsync(tenantId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Event tenant '{tenantId}' is not registered.");
                ct.ThrowIfCancellationRequested();
                if (!tenant.IsActive)
                {
                    throw new InvalidOperationException($"Event tenant '{tenantId}' is inactive.");
                }
            }

            _setter.MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _setter.MultiTenantContext = previous;
        }
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

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}

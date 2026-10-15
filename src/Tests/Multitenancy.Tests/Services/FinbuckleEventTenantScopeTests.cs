using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Extensions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Multitenancy.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Multitenancy.Tests.Services;

public sealed class FinbuckleEventTenantScopeTests
{
    private readonly StubAccessor _accessor = new();

    [Fact]
    public void Begin_Should_PreserveDedicatedConnection_When_EventTenantMatchesDrainTenant()
    {
        const string connectionString = "Host=localhost;Database=tenant_acme;Username=postgres;Password=postgres";
        var services = new ServiceCollection();
        services.AddMultiTenant<AppTenantInfo>();
        using ServiceProvider provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var setter = provider.GetRequiredService<IMultiTenantContextSetter>();
        var drainScope = new FinbuckleEventingDrainScope(accessor, setter);
        var sut = new FinbuckleEventTenantScope(accessor, setter);

        using (drainScope.Begin(new EventingDrainTarget("acme", connectionString)))
        {
            accessor.MultiTenantContext.TenantInfo.ShouldNotBeNull();
            accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBe(connectionString);

            using (sut.Begin("acme"))
            {
                accessor.MultiTenantContext.TenantInfo.ShouldNotBeNull();
                accessor.MultiTenantContext.TenantInfo.Id.ShouldBe("acme");
                accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBe(connectionString);
            }
        }
    }

    [Fact]
    public void Begin_Should_SetTenantContext_When_TenantIdProvided()
    {
        var sut = new FinbuckleEventTenantScope(_accessor, _accessor);

        using (sut.Begin("acme"))
        {
            _accessor.MultiTenantContext.TenantInfo.ShouldNotBeNull();
            _accessor.MultiTenantContext.TenantInfo.Id.ShouldBe("acme");
            _accessor.MultiTenantContext.TenantInfo.Identifier.ShouldBe("acme");
        }
    }

    [Fact]
    public void Begin_Should_RestorePreviousContext_When_Disposed()
    {
        // Seed a pre-existing ambient tenant.
        ((IMultiTenantContextSetter)_accessor).MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(new AppTenantInfo("root", "root"));
        var sut = new FinbuckleEventTenantScope(_accessor, _accessor);

        using (sut.Begin("acme"))
        {
            _accessor.MultiTenantContext.TenantInfo!.Id.ShouldBe("acme");
        }

        _accessor.MultiTenantContext.TenantInfo.Id.ShouldBe("root");
    }

    [Fact]
    public void Begin_Should_LeaveContextUntouched_When_TenantIdNullOrWhitespace()
    {
        // Seed a known tenant so we can prove Begin(null/"") does not replace it.
        ((IMultiTenantContextSetter)_accessor).MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(new AppTenantInfo("root", "root"));
        var sut = new FinbuckleEventTenantScope(_accessor, _accessor);

        using (sut.Begin(null))
        {
            _accessor.MultiTenantContext.TenantInfo!.Id.ShouldBe("root");
        }

        using (sut.Begin("   "))
        {
            _accessor.MultiTenantContext.TenantInfo.Id.ShouldBe("root");
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_KeepFullTenantAcrossAwaits_AndRestoreCaller()
    {
        var resolution = new TaskCompletionSource<AppTenantInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = Substitute.For<IEventTenantResolver>();
        using ServiceProvider provider = CreateAsyncProvider(resolver);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var setter = provider.GetRequiredService<IMultiTenantContextSetter>();
        var previous = new MultiTenantContext<AppTenantInfo>(NewTenant("source", "source-db"));
        setter.MultiTenantContext = previous;
        var tenant = NewTenant("acme", "dedicated-db");
        var sut = provider.GetRequiredService<IEventTenantScope>();
        bool called = false;
        resolver.ResolveAsync("acme", Arg.Any<CancellationToken>()).Returns(async call =>
        {
            accessor.MultiTenantContext.TenantInfo!.Id.ShouldBe(MultitenancyConstants.Root.Id);
            accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBeEmpty();
            AppTenantInfo? resolved = await resolution.Task.WaitAsync(call.Arg<CancellationToken>()).ConfigureAwait(false);
            accessor.MultiTenantContext.TenantInfo.Id.ShouldBe(MultitenancyConstants.Root.Id);
            return resolved;
        });

        Task dispatch = sut.ExecuteAsync("acme", async ct =>
        {
            called = true;
            accessor.MultiTenantContext.TenantInfo.ShouldBeSameAs(tenant);
            await Task.Yield();
            accessor.MultiTenantContext.TenantInfo.ShouldBeSameAs(tenant);
            ct.ThrowIfCancellationRequested();
        });

        dispatch.IsCompleted.ShouldBeFalse();
        called.ShouldBeFalse();
        accessor.MultiTenantContext.ShouldBeSameAs(previous);
        resolution.SetResult(tenant);
        await dispatch;
        called.ShouldBeTrue();
        accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    [Fact]
    public void Begin_Should_RestoreMatchingTenant_When_NestedWorkReplacesContext()
    {
        var previous = new MultiTenantContext<AppTenantInfo>(NewTenant("acme", "dedicated-db"));
        ((IMultiTenantContextSetter)_accessor).MultiTenantContext = previous;
        var sut = new FinbuckleEventTenantScope(_accessor, _accessor);

        using (sut.Begin("acme"))
        {
            _accessor.MultiTenantContext.ShouldBeSameAs(previous);
            ((IMultiTenantContextSetter)_accessor).MultiTenantContext =
                new MultiTenantContext<AppTenantInfo>(NewTenant("other", "other-db"));
        }

        _accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    [Theory]
    [InlineData("acme", "dedicated-db")]
    [InlineData("shared", "dedicated-db")]
    [InlineData("default", "")]
    public async Task ExecuteAsync_Should_UseCatalogMetadata_NotSourceConnection(string tenantId, string connection)
    {
        var resolver = Substitute.For<IEventTenantResolver>();
        var tenant = NewTenant(tenantId, connection);
        resolver.ResolveAsync(tenantId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<AppTenantInfo?>(tenant));
        using ServiceProvider provider = CreateAsyncProvider(resolver);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var setter = provider.GetRequiredService<IMultiTenantContextSetter>();
        var previous = new MultiTenantContext<AppTenantInfo>(NewTenant("source", "wrong-db"));
        setter.MultiTenantContext = previous;

        await provider.GetRequiredService<IEventTenantScope>().ExecuteAsync(tenantId, _ =>
        {
            accessor.MultiTenantContext.TenantInfo.ShouldBeSameAs(tenant);
            accessor.MultiTenantContext.TenantInfo.ShouldNotBeNull().ConnectionString.ShouldBe(connection);
            accessor.MultiTenantContext.TenantInfo.Name.ShouldBe("catalog-name");
            return Task.CompletedTask;
        });

        accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_Should_RouteGlobalToDefault_WithoutInheritingSource(string? tenantId)
    {
        var resolver = Substitute.For<IEventTenantResolver>();
        using ServiceProvider provider = CreateAsyncProvider(resolver);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var setter = provider.GetRequiredService<IMultiTenantContextSetter>();
        var previous = new MultiTenantContext<AppTenantInfo>(NewTenant("source", "dedicated-db"));
        setter.MultiTenantContext = previous;

        await provider.GetRequiredService<IEventTenantScope>().ExecuteAsync(tenantId, async _ =>
        {
            await Task.Yield();
            accessor.MultiTenantContext.TenantInfo!.Id.ShouldBe(MultitenancyConstants.Root.Id);
            accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBeEmpty();
        });

        accessor.MultiTenantContext.ShouldBeSameAs(previous);
        await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Should_RejectUnknownOrInactive_BeforeConsumer(bool inactive)
    {
        var resolver = Substitute.For<IEventTenantResolver>();
        AppTenantInfo? tenant = inactive ? NewTenant("target", "dedicated-db") : null;
        if (tenant is not null) tenant.IsActive = false;
        resolver.ResolveAsync("target", Arg.Any<CancellationToken>()).Returns(Task.FromResult(tenant));
        using ServiceProvider provider = CreateAsyncProvider(resolver);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var previous = accessor.MultiTenantContext;
        bool called = false;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IEventTenantScope>().ExecuteAsync("target", _ =>
            {
                called = true;
                return Task.CompletedTask;
            }));

        called.ShouldBeFalse();
        accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_Should_RestoreOnLookupOrHandlerFailure(bool duringLookup, bool cancellation)
    {
        using var cancellationSource = new CancellationTokenSource();
        var resolution = new TaskCompletionSource<AppTenantInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = Substitute.For<IEventTenantResolver>();
        resolver.ResolveAsync("target", Arg.Any<CancellationToken>())
            .Returns(call => resolution.Task.WaitAsync(call.Arg<CancellationToken>()));
        using ServiceProvider provider = CreateAsyncProvider(resolver);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var previous = accessor.MultiTenantContext;
        Task dispatch = provider.GetRequiredService<IEventTenantScope>().ExecuteAsync("target", async ct =>
        {
            entered.SetResult();
            await handled.Task.WaitAsync(ct).ConfigureAwait(false);
        }, cancellationSource.Token);

        if (!duringLookup)
        {
            resolution.SetResult(NewTenant("target", "dedicated-db"));
            await entered.Task;
        }

        if (cancellation) await cancellationSource.CancelAsync();
        else if (duringLookup) resolution.SetException(new InvalidOperationException("lookup failed"));
        else handled.SetException(new InvalidOperationException("handler failed"));

        if (cancellation) await Should.ThrowAsync<OperationCanceledException>(() => dispatch);
        else await Should.ThrowAsync<InvalidOperationException>(() => dispatch);
        entered.Task.IsCompleted.ShouldBe(!duringLookup);
        accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    [Fact]
    public async Task ExecuteAsync_Should_IsolateConcurrentTenants_SharingDedicatedDatabase()
    {
        var resolver = Substitute.For<IEventTenantResolver>();
        var resolution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await resolution.Task.WaitAsync(call.Arg<CancellationToken>()).ConfigureAwait(false);
            return (AppTenantInfo?)NewTenant(call.Arg<string>(), "shared-dedicated-db");
        });
        using ServiceProvider provider = CreateAsyncProvider(resolver);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var previous = accessor.MultiTenantContext;
        var sut = provider.GetRequiredService<IEventTenantScope>();

        async Task HandleAsync(string tenantId, TaskCompletionSource entered, Task otherEntered)
        {
            accessor.MultiTenantContext.TenantInfo!.Id.ShouldBe(tenantId);
            entered.SetResult();
            await otherEntered.ConfigureAwait(false);
            accessor.MultiTenantContext.TenantInfo.Id.ShouldBe(tenantId);
            accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBe("shared-dedicated-db");
        }

        Task first = sut.ExecuteAsync("first", _ => HandleAsync("first", firstEntered, secondEntered.Task));
        Task second = sut.ExecuteAsync("second", _ => HandleAsync("second", secondEntered, firstEntered.Task));
        resolution.SetResult();
        await Task.WhenAll(first, second);
        accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    private static AppTenantInfo NewTenant(string id, string connection) => new(id, id, connection, "event@example.test")
    {
        Name = "catalog-name",
        Plan = "pro"
    };

    private static ServiceProvider CreateAsyncProvider(IEventTenantResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddMultiTenant<AppTenantInfo>();
        services.AddSingleton(resolver);
        services.AddSingleton<IEventTenantScope, FinbuckleEventTenantScope>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

#pragma warning disable S2376 // Finbuckle's IMultiTenantContextSetter is a set-only contract.
    private sealed class StubAccessor : IMultiTenantContextAccessor<AppTenantInfo>, IMultiTenantContextSetter
    {
        private IMultiTenantContext<AppTenantInfo> _context =
            new MultiTenantContext<AppTenantInfo>(new AppTenantInfo());

        public IMultiTenantContext<AppTenantInfo> MultiTenantContext => _context;

        IMultiTenantContext IMultiTenantContextAccessor.MultiTenantContext => _context;

        IMultiTenantContext IMultiTenantContextSetter.MultiTenantContext
        {
            set => _context = (IMultiTenantContext<AppTenantInfo>)value;
        }
    }
#pragma warning restore S2376
}

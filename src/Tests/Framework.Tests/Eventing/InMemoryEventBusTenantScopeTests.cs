using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.InMemory;
using FSH.Framework.Eventing.Inbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framework.Tests.Eventing;

/// <summary>
/// Guards the systemic fix for the background-dispatch tenant-context bug: the bus must
/// establish the tenant scope from the event's <see cref="IIntegrationEvent.TenantId"/>
/// BEFORE it resolves handlers (which materialize tenant-filtered DbContexts). Without
/// this, handlers dispatched from the outbox NRE in their tenant query filter.
/// </summary>
public sealed class InMemoryEventBusTenantScopeTests
{
    [Fact]
    public async Task PublishAsync_Should_BeginTenantScope_WithEventTenantId_WhileHandlerRuns()
    {
        // Arrange
        var scope = new RecordingTenantScope();
        var handler = new TenantProbingHandler(scope);

        var services = new ServiceCollection();
        services.AddSingleton<IEventTenantScope>(scope);
        services.AddSingleton<IIntegrationEventHandler<TenantScopedEvent>>(handler);
        using var provider = services.BuildServiceProvider();

        var bus = new InMemoryEventBus(provider, NullLogger<InMemoryEventBus>.Instance, scope);

        // Act
        await bus.PublishAsync(new TenantScopedEvent("acme"));

        // Assert — scope begun with the event's tenant, and it was still active when the
        // handler executed (i.e. before resolution, restored after).
        scope.BegunWith.ShouldHaveSingleItem().ShouldBe("acme");
        handler.ScopeWasActiveDuringHandle.ShouldBeTrue();
        scope.IsActive.ShouldBeFalse("the scope must be disposed once dispatch completes");
    }

    [Fact]
    public async Task PublishAsync_Should_ConstructHandlerAndInboxOnlyAfterAsyncLookup()
    {
        var lookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = new RecordingTenantScope();
        var tenantScope = new DelayedTenantScope(recording, lookup.Task);
        int handlerConstructions = 0;
        int inboxConstructions = 0;
        var inbox = Substitute.For<IInboxStore>();
        TenantProbingHandler? handler = null;
        var services = new ServiceCollection();
        services.AddScoped<IIntegrationEventHandler<TenantScopedEvent>>(_ =>
        {
            recording.IsActive.ShouldBeTrue();
            recording.BegunWith.ShouldHaveSingleItem().ShouldBe("acme");
            handlerConstructions++;
            handler = new TenantProbingHandler(recording);
            return handler;
        });
        services.AddScoped<IInboxStore>(_ =>
        {
            recording.IsActive.ShouldBeTrue();
            inboxConstructions++;
            return inbox;
        });
        using var provider = services.BuildServiceProvider();
        var bus = new InMemoryEventBus(provider, NullLogger<InMemoryEventBus>.Instance, tenantScope);
        var integrationEvent = new TenantScopedEvent("acme");
        using var cancellation = new CancellationTokenSource();

        Task dispatch = bus.PublishAsync(integrationEvent, cancellation.Token);
        dispatch.IsCompleted.ShouldBeFalse();
        handlerConstructions.ShouldBe(0);
        inboxConstructions.ShouldBe(0);
        lookup.SetResult();
        await dispatch;

        handlerConstructions.ShouldBe(1);
        inboxConstructions.ShouldBe(1);
        handler.ShouldNotBeNull().ScopeWasActiveDuringHandle.ShouldBeTrue();
        recording.IsActive.ShouldBeFalse();
        await inbox.Received(1).MarkProcessedAsync(integrationEvent.Id, typeof(TenantProbingHandler).FullName!,
            "acme", typeof(TenantScopedEvent).AssemblyQualifiedName!, cancellation.Token);
    }

    [Fact]
    public async Task PublishAsync_Should_NotConstructConsumers_When_DefaultBridgeIsCancelled()
    {
        var recording = new RecordingTenantScope();
        int constructions = 0;
        var services = new ServiceCollection();
        services.AddScoped<IIntegrationEventHandler<TenantScopedEvent>>(_ =>
        {
            constructions++;
            return new TenantProbingHandler(recording);
        });
        using var provider = services.BuildServiceProvider();
        var bus = new InMemoryEventBus(provider, NullLogger<InMemoryEventBus>.Instance, recording);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => bus.PublishAsync(new TenantScopedEvent("acme"), cancellation.Token));

        constructions.ShouldBe(0);
        recording.BegunWith.ShouldBeEmpty();
    }

    #region Test doubles

    private sealed class DelayedTenantScope(RecordingTenantScope inner, Task lookup) : IEventTenantScope
    {
        public IDisposable Begin(string? tenantId) => throw new InvalidOperationException("Use async dispatch.");

        public async Task ExecuteAsync(string? tenantId, Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            await lookup.WaitAsync(ct).ConfigureAwait(false);
            using (inner.Begin(tenantId))
            {
                await action(ct).ConfigureAwait(false);
            }
        }
    }

    private sealed record TenantScopedEvent(string? TenantId) : IIntegrationEvent
    {
        public Guid Id { get; } = Guid.CreateVersion7();
        public DateTime OccurredOnUtc { get; } = DateTime.UtcNow;
        public string CorrelationId { get; } = Guid.CreateVersion7().ToString();
        public string Source { get; } = "tests";
    }

    private sealed class RecordingTenantScope : IEventTenantScope
    {
        public List<string?> BegunWith { get; } = [];
        public bool IsActive { get; private set; }

        public IDisposable Begin(string? tenantId)
        {
            BegunWith.Add(tenantId);
            IsActive = true;
            return new Handle(this);
        }

        private sealed class Handle(RecordingTenantScope owner) : IDisposable
        {
            public void Dispose() => owner.IsActive = false;
        }
    }

    private sealed class TenantProbingHandler(RecordingTenantScope scope)
        : IIntegrationEventHandler<TenantScopedEvent>
    {
        public bool ScopeWasActiveDuringHandle { get; private set; }

        public async Task HandleAsync(TenantScopedEvent @event, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            ScopeWasActiveDuringHandle = scope.IsActive;
        }
    }

    #endregion
}

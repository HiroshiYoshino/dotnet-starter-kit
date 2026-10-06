using System.Collections.Concurrent;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Catalog.Data;
using Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Integration.Tests.Tests.Eventing;

[Collection(FshCollectionDefinition.Name)]
public sealed class OutboxDedicatedTenantDispatchTests
{
    private readonly FshWebApplicationFactory _factory;

    public OutboxDedicatedTenantDispatchTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Dispatch_Should_RouteHandlersByEventTenant_AndCompleteOutboxInSourceDatabase()
    {
        string defaultConnectionString = _factory.Services
            .GetRequiredService<IOptions<DatabaseOptions>>()
            .Value.ConnectionString;
        NpgsqlConnectionStringBuilder defaultBuilder = new(defaultConnectionString);
        string defaultDatabaseName = defaultBuilder.Database
            ?? throw new InvalidOperationException("The default PostgreSQL database name is missing.");
        string suffix = Guid.CreateVersion7().ToString("N");
        string sourceDatabaseName = $"outbox_source_{suffix}";
        string targetDatabaseName = $"outbox_target_{suffix}";
        NpgsqlConnectionStringBuilder adminBuilder = new(defaultBuilder.ConnectionString)
        {
            Database = "postgres",
        };
        NpgsqlConnectionStringBuilder sourceBuilder = new(defaultBuilder.ConnectionString)
        {
            Database = sourceDatabaseName,
        };
        NpgsqlConnectionStringBuilder targetBuilder = new(defaultBuilder.ConnectionString)
        {
            Database = targetDatabaseName,
        };
        var accessor = _factory.Services.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var previous = accessor.MultiTenantContext;
        var eventIds = new List<Guid>();

        try
        {
            await CreateDatabaseAsync(adminBuilder.ConnectionString, sourceDatabaseName);
            await CreateDatabaseAsync(adminBuilder.ConnectionString, targetDatabaseName);

            AppTenantInfo sourceTenant = NewTenant(sourceDatabaseName, sourceBuilder.ConnectionString);
            AppTenantInfo targetTenant = NewTenant(targetDatabaseName, targetBuilder.ConnectionString);
            await AddTenantsAsync(_factory.Services, sourceTenant, targetTenant);
            await MigrateTenantDatabaseAsync(_factory.Services, sourceTenant);
            await MigrateTenantDatabaseAsync(_factory.Services, targetTenant);

            DatabaseRoutingRecorder recorder = new();
            await using ServiceProvider app = CreateProvider(defaultConnectionString, recorder);
            DatabaseRoutingIntegrationEvent[] events =
            [
                NewEvent(sourceTenant.Id),
                NewEvent(targetTenant.Id),
                NewEvent(null),
            ];
            eventIds.AddRange(events.Select(integrationEvent => integrationEvent.Id));

            using (IServiceScope writeScope = app.CreateScope())
            {
                writeScope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
                    .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(sourceTenant);
                IOutboxStore outbox = writeScope.ServiceProvider.GetRequiredService<IOutboxStore>();

                foreach (DatabaseRoutingIntegrationEvent @event in events)
                {
                    await outbox.AddAsync(@event, CancellationToken.None);
                }
            }

            EventingDrainTarget sourceTarget;
            using (IServiceScope targetProviderScope = app.CreateScope())
            {
                var targets = await targetProviderScope.ServiceProvider
                    .GetRequiredService<IEventingDrainTargetProvider>()
                    .GetTargetsAsync(CancellationToken.None);
                sourceTarget = targets.Single(target =>
                    string.Equals(target.ConnectionString, sourceBuilder.ConnectionString, StringComparison.Ordinal));
            }

            IEventingDrainScope drainScope = app.GetRequiredService<IEventingDrainScope>();
            using (drainScope.Begin(sourceTarget))
            {
                using (IServiceScope dispatchScope = app.CreateScope())
                {
                    EventingDbContext sourceOutbox = dispatchScope.ServiceProvider.GetRequiredService<EventingDbContext>();
                    sourceOutbox.Database.GetDbConnection().Database.ShouldBe(sourceDatabaseName);

                    await dispatchScope.ServiceProvider
                        .GetRequiredService<OutboxDispatcher>()
                        .DispatchAsync(CancellationToken.None);
                }

                using IServiceScope verifyScope = app.CreateScope();
                EventingDbContext verifyOutbox = verifyScope.ServiceProvider.GetRequiredService<EventingDbContext>();
                List<OutboxMessage> messages = await verifyOutbox.OutboxMessages
                    .AsNoTracking()
                    .Where(message => events.Select(@event => @event.Id).Contains(message.Id))
                    .ToListAsync();

                messages.Count.ShouldBe(events.Length);
                messages.ShouldAllBe(message => message.ProcessedOnUtc != null && !message.IsDead);
            }

            recorder.Count.ShouldBe(events.Length);
            recorder.DatabaseFor(events[0].Id).ShouldBe(sourceDatabaseName);
            recorder.DatabaseFor(events[1].Id).ShouldBe(targetDatabaseName);
            recorder.DatabaseFor(events[2].Id).ShouldBe(defaultDatabaseName);
        }
        finally
        {
            _factory.Services.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = previous;
            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
                await store.RemoveAsync(sourceDatabaseName);
                await store.RemoveAsync(targetDatabaseName);
            }
            await using (var connection = new NpgsqlConnection(defaultConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("DELETE FROM framework.\"InboxMessages\" WHERE \"Id\" = ANY(@ids)", connection);
                command.Parameters.AddWithValue("ids", eventIds.ToArray());
                await command.ExecuteNonQueryAsync();
            }
            await DropDatabaseAsync(adminBuilder.ConnectionString, targetDatabaseName);
            await DropDatabaseAsync(adminBuilder.ConnectionString, sourceDatabaseName);
        }
    }

    private ServiceProvider CreateProvider(string defaultConnection, DatabaseRoutingRecorder recorder) =>
        DedicatedTenantOutboxRoutingTests.CreateProvider(_factory.Services, defaultConnection, services =>
        {
            services.AddSingleton(recorder);
            services.AddScoped<IIntegrationEventHandler<DatabaseRoutingIntegrationEvent>, DatabaseRoutingHandler>();
        });

    private static async Task AddTenantsAsync(IServiceProvider services, params AppTenantInfo[] tenants)
    {
        using IServiceScope scope = services.CreateScope();
        IMultiTenantStore<AppTenantInfo> store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();

        foreach (AppTenantInfo tenant in tenants)
        {
            await store.AddAsync(tenant);
        }
    }

    private static async Task MigrateTenantDatabaseAsync(IServiceProvider services, AppTenantInfo tenant)
    {
        using IServiceScope scope = services.CreateScope();
        IServiceProvider provider = scope.ServiceProvider;
        provider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        foreach (IDbInitializer initializer in provider.GetServices<IDbInitializer>())
        {
            await initializer.MigrateAsync(CancellationToken.None);
        }
    }

    private static AppTenantInfo NewTenant(string id, string connectionString) =>
        new(id, id, connectionString, "outbox-test@example.test")
        {
            IsActive = true,
        };

    private static DatabaseRoutingIntegrationEvent NewEvent(string? tenantId)
    {
        Guid id = Guid.CreateVersion7();
        return new DatabaseRoutingIntegrationEvent(
            id,
            DateTime.UtcNow,
            tenantId,
            $"outbox-routing-{id:N}",
            "Integration.Tests");
    }

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        using NpgsqlCommandBuilder commandBuilder = new();
    #pragma warning disable CA2100 // The test database name is generated locally and identifier-quoted.
        command.CommandText = $"CREATE DATABASE {commandBuilder.QuoteIdentifier(databaseName)}";
    #pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        using NpgsqlCommandBuilder commandBuilder = new();
    #pragma warning disable CA2100 // The test database name is generated locally and identifier-quoted.
        command.CommandText = $"DROP DATABASE IF EXISTS {commandBuilder.QuoteIdentifier(databaseName)} WITH (FORCE)";
    #pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    public sealed record DatabaseRoutingIntegrationEvent(
        Guid Id,
        DateTime OccurredOnUtc,
        string? TenantId,
        string CorrelationId,
        string Source) : IIntegrationEvent;

    public sealed class DatabaseRoutingHandler(
        CatalogDbContext catalogDbContext,
        DatabaseRoutingRecorder recorder)
        : IIntegrationEventHandler<DatabaseRoutingIntegrationEvent>
    {
        public async Task HandleAsync(DatabaseRoutingIntegrationEvent @event, CancellationToken ct = default)
        {
            (await catalogDbContext.Database.CanConnectAsync(ct).ConfigureAwait(false)).ShouldBeTrue();
            recorder.Record(@event.Id, catalogDbContext.Database.GetDbConnection().Database);
        }
    }

    public sealed class DatabaseRoutingRecorder
    {
        private readonly ConcurrentDictionary<Guid, string> _databaseNames = new();

        public int Count => _databaseNames.Count;

        public void Record(Guid eventId, string databaseName) => _databaseNames[eventId] = databaseName;

        public string? DatabaseFor(Guid eventId) =>
            _databaseNames.TryGetValue(eventId, out string? databaseName) ? databaseName : null;
    }
}
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore.Stores;
using Finbuckle.MultiTenant.Extensions;
using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Catalog.Data;
using FSH.Modules.Catalog.Domain;
using FSH.Modules.Files.Contracts.Events;
using FSH.Modules.Multitenancy.Data;
using FSH.Modules.Multitenancy.Services;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Integration.Tests.Tests.Eventing;

[Collection(FshCollectionDefinition.Name)]
public sealed class DedicatedTenantOutboxRoutingTests
{
    private readonly FshWebApplicationFactory _factory;

    public DedicatedTenantOutboxRoutingTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DispatchAsync_Should_PersistConsumerAndInboxOnlyInDedicatedDatabase_When_SourceTenantMatchesEventTenant()
    {
        string defaultConnection = _factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
        string databaseName = $"outbox_dispatch_{Guid.CreateVersion7():N}";
        string tenantId = $"dispatch-{Guid.CreateVersion7():N}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(defaultConnection) { Database = databaseName };
        string dedicatedConnection = connectionBuilder.ConnectionString;
        var tenant = new AppTenantInfo(tenantId, tenantId, dedicatedConnection, "dispatch@example.test");
        Guid eventId = Guid.CreateVersion7();
        var integrationEvent = new FileFinalizedIntegrationEvent(
            eventId, DateTime.UtcNow, tenantId, $"corr-{eventId:N}", "Files",
            Guid.CreateVersion7(), "Test", null, "application/octet-stream", 1, 1);
        await using ServiceProvider provider = BuildProvider(defaultConnection);
        var accessor = provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        var setter = provider.GetRequiredService<IMultiTenantContextSetter>();
        var previousContext = accessor.MultiTenantContext;
        var drainScope = provider.GetRequiredService<IEventingDrainScope>();
        bool databaseCreated = false;

        try
        {
            await ExecuteDatabaseCommandAsync(defaultConnection, $"CREATE DATABASE {QuoteIdentifier(databaseName)}");
            databaseCreated = true;

            using (drainScope.Begin(new EventingDrainTarget(tenantId, dedicatedConnection)))
            using (var scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<EventingDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<IOutboxWriter>().AddAsync(integrationEvent);
            }

            using (var scope = provider.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
                (await store.AddAsync(tenant)).ShouldBeTrue();
                var targets = await scope.ServiceProvider.GetRequiredService<IEventingDrainTargetProvider>().GetTargetsAsync();
                targets.Any(target => target.TenantId is null && target.ConnectionString is null).ShouldBeTrue();
                EventingDrainTarget target = targets.Single(target => target.TenantId == tenantId);
                target.ConnectionString.ShouldBe(dedicatedConnection);

                using (drainScope.Begin(target))
                using (var sourceScope = provider.CreateScope())
                {
                    await sourceScope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
                }
            }

            DatabaseSnapshot dedicated = await ReadSnapshotAsync(dedicatedConnection, integrationEvent);
            DatabaseSnapshot shared = await ReadSnapshotAsync(defaultConnection, integrationEvent);

            using (drainScope.Begin(new EventingDrainTarget(tenantId, dedicatedConnection)))
            using (var sourceScope = provider.CreateScope())
            {
                var context = sourceScope.ServiceProvider.GetRequiredService<EventingDbContext>();
                await context.OutboxMessages.Where(message => message.Id == eventId)
                    .ExecuteUpdateAsync(update => update.SetProperty(message => message.ProcessedOnUtc, (DateTime?)null));
                await sourceScope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            }

            DatabaseSnapshot dedicatedAfterRedelivery = await ReadSnapshotAsync(dedicatedConnection, integrationEvent);
            DatabaseSnapshot sharedAfterRedelivery = await ReadSnapshotAsync(defaultConnection, integrationEvent);

            Assert.Multiple(
                () => dedicated.ConsumerRows.ShouldBe(1, "the tenant-aware consumer must write to the dedicated database"),
                () => dedicated.ConsumerTenantRows.ShouldBe(1, "the business row must belong to the event tenant"),
                () => shared.ConsumerRows.ShouldBe(0, "the default database must not receive a dedicated tenant's side effect"),
                () => dedicated.InboxRows.ShouldBe(1, "the consumer's Inbox must be recorded in the dedicated database"),
                () => dedicated.InboxTenantRows.ShouldBe(1, "the Inbox row must belong to the event tenant"),
                () => shared.InboxRows.ShouldBe(0, "the default database must not receive the consumer's Inbox"),
                () => dedicated.OutboxRows.ShouldBe(1),
                () => dedicated.ProcessedRows.ShouldBe(1, "completion must update the original source Outbox"),
                () => dedicated.ClaimedRows.ShouldBe(0),
                () => dedicated.Retries.ShouldBe(0),
                () => shared.OutboxRows.ShouldBe(0),
                () => dedicatedAfterRedelivery.ShouldBe(dedicated, "Inbox must suppress duplicate business writes on redelivery"),
                () => sharedAfterRedelivery.ShouldBe(shared, "redelivery must not introduce default-database side effects"));
        }
        finally
        {
            setter.MultiTenantContext = previousContext;
            using (var scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().RemoveAsync(tenantId);
            }

            await CleanupDefaultRowsAsync(defaultConnection, integrationEvent);
            if (databaseCreated)
            {
                await ExecuteDatabaseCommandAsync(defaultConnection, $"DROP DATABASE {QuoteIdentifier(databaseName)} WITH (FORCE)");
            }
        }
    }

    private ServiceProvider BuildProvider(string defaultConnection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EventingOptions:Provider"] = "InMemory",
            ["EventingOptions:UseHostedServiceDispatcher"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(_factory.Services.GetRequiredService<IHostEnvironment>());
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.Configure<DatabaseOptions>(options =>
        {
            options.Provider = "POSTGRESQL";
            options.ConnectionString = defaultConnection;
            options.MigrationsAssembly = "FSH.Starter.Migrations.PostgreSQL";
        });
        services.AddScoped<IScopedDbConnectionProvider, ScopedDbConnectionProvider>();
        services.AddScoped<AmbientDbTransactionRegistry>();
        services.AddDbContext<TenantDbContext>(options => options.UseNpgsql(defaultConnection));
        services.AddMultiTenant<AppTenantInfo>()
            .WithStore<EFCoreStore<TenantDbContext, AppTenantInfo>>(ServiceLifetime.Scoped);
        services.AddEventingCore(configuration);
        services.AddHeroDbContext<CatalogDbContext>();
        services.Replace(ServiceDescriptor.Singleton<IEventTenantScope, FinbuckleEventTenantScope>());
        services.Replace(ServiceDescriptor.Singleton<IEventingDrainScope, FinbuckleEventingDrainScope>());
        services.Replace(ServiceDescriptor.Scoped<IEventingDrainTargetProvider, TenantStoreDrainTargetProvider>());
        services.AddScoped<IIntegrationEventHandler<FileFinalizedIntegrationEvent>, FileEventMarkerHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static string QuoteIdentifier(string identifier)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(identifier);
    }

    private static async Task ExecuteDatabaseCommandAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
#pragma warning disable CA2100
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(string connectionString, FileFinalizedIntegrationEvent integrationEvent)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM catalog."Brands" WHERE "Name" = @marker),
                (SELECT COUNT(*) FROM catalog."Brands" WHERE "Name" = @marker AND "TenantId" = @tenant),
                (SELECT COUNT(*) FROM framework."InboxMessages" WHERE "Id" = @event AND "HandlerName" = @handler),
                (SELECT COUNT(*) FROM framework."InboxMessages" WHERE "Id" = @event AND "HandlerName" = @handler AND "TenantId" = @tenant),
                (SELECT COUNT(*) FROM framework."OutboxMessages" WHERE "Id" = @event),
                (SELECT COUNT(*) FROM framework."OutboxMessages" WHERE "Id" = @event AND "ProcessedOnUtc" IS NOT NULL),
                (SELECT COUNT(*) FROM framework."OutboxMessages" WHERE "Id" = @event AND "ClaimedUntilUtc" IS NOT NULL),
                (SELECT COALESCE(SUM("RetryCount"), 0) FROM framework."OutboxMessages" WHERE "Id" = @event)
            """, connection);
        command.Parameters.AddWithValue("marker", $"dispatch-{integrationEvent.Id:N}");
        command.Parameters.AddWithValue("tenant", integrationEvent.TenantId!);
        command.Parameters.AddWithValue("event", integrationEvent.Id);
        command.Parameters.AddWithValue("handler", typeof(FileEventMarkerHandler).FullName!);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new DatabaseSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private static async Task CleanupDefaultRowsAsync(string connectionString, FileFinalizedIntegrationEvent integrationEvent)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM catalog."Brands" WHERE "Name" = @marker;
            DELETE FROM framework."InboxMessages" WHERE "Id" = @event;
            DELETE FROM framework."OutboxMessages" WHERE "Id" = @event;
            """, connection);
        command.Parameters.AddWithValue("marker", $"dispatch-{integrationEvent.Id:N}");
        command.Parameters.AddWithValue("event", integrationEvent.Id);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record DatabaseSnapshot(long ConsumerRows, long ConsumerTenantRows, long InboxRows,
        long InboxTenantRows, long OutboxRows, long ProcessedRows, long ClaimedRows, long Retries);

    public sealed class FileEventMarkerHandler(CatalogDbContext context) : IIntegrationEventHandler<FileFinalizedIntegrationEvent>
    {
        public async Task HandleAsync(FileFinalizedIntegrationEvent integrationEvent, CancellationToken ct = default)
        {
            context.Brands.Add(Brand.Create($"dispatch-{integrationEvent.Id:N}", null, null));
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
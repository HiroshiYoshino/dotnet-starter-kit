using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore.Stores;
using Finbuckle.MultiTenant.Extensions;
using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Inbox;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Catalog.Data;
using FSH.Modules.Catalog.Domain;
using FSH.Modules.Billing.Data;
using FSH.Modules.Billing.Domain;
using FSH.Modules.Billing.Services;
using FSH.Modules.Billing.IntegrationEventHandlers;
using FSH.Modules.Files.Contracts.Events;
using FSH.Modules.Multitenancy.Contracts.Events;
using FSH.Modules.Multitenancy.Data;
using FSH.Modules.Multitenancy.Services;
using FSH.Modules.Webhooks.Data;
using FSH.Modules.Webhooks.Domain;
using FSH.Modules.Webhooks.Services;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Collections.Concurrent;

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

            await CleanupDefaultRowsAsync(defaultConnection, integrationEvent.Id);
            if (databaseCreated)
            {
                await ExecuteDatabaseCommandAsync(defaultConnection, $"DROP DATABASE {QuoteIdentifier(databaseName)} WITH (FORCE)");
            }
        }
    }

    [Fact]
    public async Task PublishAsync_Should_ConstructConsumersWithFullTenantOnlyAfterPendingCatalogLookup()
    {
        var lookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppTenantInfo? expected = null;
        int handlerConstructions = 0;
        int inboxConstructions = 0;
        void ObserveTenant(IServiceProvider services)
        {
            AppTenantInfo tenant = services.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>()
                .MultiTenantContext.TenantInfo.ShouldNotBeNull();
            tenant.Id.ShouldBe(expected.ShouldNotBeNull().Id);
            tenant.ConnectionString.ShouldBe(expected.ConnectionString);
            tenant.Name.ShouldBe("pending-catalog-name");
            tenant.Plan.ShouldBe("pro");
        }

        await using RoutingEnvironment environment = await CreateEnvironmentAsync(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IEventTenantResolver>(consumer => new PendingTenantResolver(
                new EventTenantResolver(consumer.GetRequiredService<TenantDbContext>()),
                consumer.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>(), lookup.Task)));
            services.Replace(ServiceDescriptor.Scoped<IIntegrationEventHandler<FileFinalizedIntegrationEvent>>(consumer =>
            {
                ObserveTenant(consumer);
                handlerConstructions++;
                return new ObservingFileHandler(new FileEventMarkerHandler(consumer.GetRequiredService<CatalogDbContext>()),
                    () => ObserveTenant(consumer));
            }));
            services.Replace(ServiceDescriptor.Scoped<IInboxStore>(consumer =>
            {
                ObserveTenant(consumer);
                inboxConstructions++;
                return ActivatorUtilities.CreateInstance<EfCoreInboxStore>(consumer);
            }));
        });
        expected = environment.DedicatedTenant;
        using (var scope = environment.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Set<AppTenantInfo>()
                .Where(tenant => tenant.Id == expected.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(tenant => tenant.Name, "pending-catalog-name")
                    .SetProperty(tenant => tenant.Plan, "pro"));
        }
        var integrationEvent = NewFileEvent(expected.Id);
        environment.Events.Add(integrationEvent);
        using (environment.Drain.Begin(environment.DedicatedTarget))
        {
            var previous = environment.Accessor.MultiTenantContext;
            Task dispatch = environment.Provider.GetRequiredService<IEventBus>().PublishAsync(integrationEvent);
            dispatch.IsCompleted.ShouldBeFalse();
            handlerConstructions.ShouldBe(0);
            inboxConstructions.ShouldBe(0);
            environment.Accessor.MultiTenantContext.ShouldBeSameAs(previous);
            lookup.SetResult();
            await dispatch;
            environment.Accessor.MultiTenantContext.ShouldBeSameAs(previous);
        }
        handlerConstructions.ShouldBe(1);
        inboxConstructions.ShouldBe(1);
        string handlerName = typeof(ObservingFileHandler).FullName!;
        DatabaseSnapshot dedicated = await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent, handlerName);
        dedicated.ConsumerTenantRows.ShouldBe(1);
        dedicated.InboxTenantRows.ShouldBe(1);
        DatabaseSnapshot shared = await ReadSnapshotAsync(environment.DefaultConnection, integrationEvent, handlerName);
        shared.ConsumerRows.ShouldBe(0);
        shared.InboxRows.ShouldBe(0);
    }

    [Theory]
    [InlineData("dedicated", true)]
    [InlineData("shared", false)]
    [InlineData("default", true)]
    [InlineData("global", false)]
    public async Task DispatchAsync_Should_SeparateSourceFromConsumerDatabase(string destination, bool defaultSource)
    {
        await using RoutingEnvironment environment = await CreateEnvironmentAsync();
        string? tenantId = destination switch
        {
            "dedicated" => environment.DedicatedTenant.Id,
            "shared" => environment.SharedTenant.Id,
            "default" => environment.DefaultTenant.Id,
            _ => null
        };
        var integrationEvent = NewFileEvent(tenantId);
        environment.Events.Add(integrationEvent);
        var source = defaultSource ? environment.DefaultTarget : environment.DedicatedTarget;
        using (environment.Drain.Begin(source))
        using (var scope = environment.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxWriter>().AddAsync(integrationEvent);
            var previous = environment.Accessor.MultiTenantContext;
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            environment.Accessor.MultiTenantContext.ShouldBeSameAs(previous);
        }

        DatabaseSnapshot dedicated = await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent);
        DatabaseSnapshot shared = await ReadSnapshotAsync(environment.DefaultConnection, integrationEvent);
        bool dedicatedConsumer = destination is "dedicated" or "shared";
        dedicated.ConsumerRows.ShouldBe(dedicatedConsumer ? 1 : 0);
        dedicated.ConsumerTenantRows.ShouldBe(dedicated.ConsumerRows);
        dedicated.InboxRows.ShouldBe(dedicated.ConsumerRows);
        dedicated.InboxTenantRows.ShouldBe(dedicated.ConsumerRows);
        shared.ConsumerRows.ShouldBe(dedicatedConsumer ? 0 : 1);
        shared.ConsumerTenantRows.ShouldBe(shared.ConsumerRows);
        shared.InboxRows.ShouldBe(shared.ConsumerRows);
        shared.InboxTenantRows.ShouldBe(shared.ConsumerRows);
        dedicated.OutboxRows.ShouldBe(defaultSource ? 0 : 1);
        dedicated.ProcessedRows.ShouldBe(dedicated.OutboxRows);
        shared.OutboxRows.ShouldBe(defaultSource ? 1 : 0);
        shared.ProcessedRows.ShouldBe(shared.OutboxRows);
        dedicated.ClaimedRows.ShouldBe(0);
        shared.ClaimedRows.ShouldBe(0);
        dedicated.Retries.ShouldBe(0);
        shared.Retries.ShouldBe(0);
        using (environment.Drain.Begin(source))
        using (var scope = environment.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventingDbContext>().OutboxMessages
                .Where(message => message.Id == integrationEvent.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(message => message.ProcessedOnUtc, (DateTime?)null));
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
        }
        (await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent)).ShouldBe(dedicated);
        (await ReadSnapshotAsync(environment.DefaultConnection, integrationEvent)).ShouldBe(shared);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DispatchAsync_Should_RetryThenDeadLetterRejectedEvents_WithoutConsumerWrites(bool inactive, bool consumerFailure)
    {
        await using RoutingEnvironment environment = await CreateEnvironmentAsync(services =>
        {
            if (consumerFailure)
            {
                services.RemoveAll<IIntegrationEventHandler<FileFinalizedIntegrationEvent>>();
                services.AddScoped<IIntegrationEventHandler<FileFinalizedIntegrationEvent>, FailingFileHandler>();
            }
        });
        string tenantId = inactive || consumerFailure ? environment.DedicatedTenant.Id : $"unknown-{Guid.CreateVersion7():N}";
        if (inactive)
        {
            using var scope = environment.Provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Set<AppTenantInfo>()
                .Where(tenant => tenant.Id == tenantId).ExecuteUpdateAsync(update => update.SetProperty(tenant => tenant.IsActive, false));
        }
        var integrationEvent = NewFileEvent(tenantId);
        environment.Events.Add(integrationEvent);
        environment.Provider.GetRequiredService<IOptions<EventingOptions>>().Value.OutboxMaxRetries = 2;

        using (environment.Drain.Begin(environment.DedicatedTarget))
        {
            using (var scope = environment.Provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IOutboxWriter>().AddAsync(integrationEvent);
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            }

            using (var scope = environment.Provider.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
                OutboxMessage message = await context.OutboxMessages.SingleAsync(message => message.Id == integrationEvent.Id);
                message.ProcessedOnUtc.ShouldBeNull();
                message.RetryCount.ShouldBe(1);
                message.NextRetryAt.ShouldNotBeNull();
                message.ClaimedUntilUtc.ShouldBeNull();
                message.IsDead.ShouldBeFalse();
                message.NextRetryAt = null;
                await context.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            }

            using var verify = environment.Provider.CreateScope();
            OutboxMessage failed = await verify.ServiceProvider.GetRequiredService<EventingDbContext>()
                .OutboxMessages.SingleAsync(message => message.Id == integrationEvent.Id);
            failed.ProcessedOnUtc.ShouldBeNull();
            failed.RetryCount.ShouldBe(2);
            failed.IsDead.ShouldBeTrue();
            failed.NextRetryAt.ShouldBeNull();
            failed.ClaimedUntilUtc.ShouldBeNull();
        }

        DatabaseSnapshot dedicated = await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent);
        DatabaseSnapshot shared = await ReadSnapshotAsync(environment.DefaultConnection, integrationEvent);
        dedicated.ConsumerRows.ShouldBe(0);
        dedicated.InboxRows.ShouldBe(0);
        shared.ConsumerRows.ShouldBe(0);
        shared.InboxRows.ShouldBe(0);
        shared.OutboxRows.ShouldBe(0);
        if (consumerFailure)
        {
            (await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent, typeof(FailingFileHandler).FullName!)).InboxRows.ShouldBe(0);
            (await ReadSnapshotAsync(environment.DefaultConnection, integrationEvent, typeof(FailingFileHandler).FullName!)).InboxRows.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Begin_Should_SelectDefaultDrain_WithoutLeakingDedicatedConnection_AndRestoreSource()
    {
        await using RoutingEnvironment environment = await CreateEnvironmentAsync();
        var previous = environment.Accessor.MultiTenantContext;
        using (environment.Drain.Begin(environment.DedicatedTarget))
        {
            var dedicated = environment.Accessor.MultiTenantContext;
            using (environment.Drain.Begin(new EventingDrainTarget(null, null)))
            using (var scope = environment.Provider.CreateScope())
            {
                environment.Accessor.MultiTenantContext.TenantInfo.ShouldNotBeNull().Id.ShouldBe(MultitenancyConstants.Root.Id);
                environment.Accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBeEmpty();
                scope.ServiceProvider.GetRequiredService<EventingDbContext>().Database.GetDbConnection().Database
                    .ShouldBe(new NpgsqlConnectionStringBuilder(environment.DefaultConnection).Database);
            }
            environment.Accessor.MultiTenantContext.ShouldBeSameAs(dedicated);
        }
        environment.Accessor.MultiTenantContext.ShouldBeSameAs(previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_Should_ReadDedicatedWebhookSubscriptions_AndDeduplicateRedelivery(bool global)
    {
        var recorder = new WebhookRecorder();
        await using RoutingEnvironment environment = await CreateEnvironmentAsync(services => AddWebhookServices(services, recorder));
        var integrationEvent = NewFileEvent(global ? null : environment.DedicatedTenant.Id);
        environment.Events.Add(integrationEvent);
        Guid correct = await SeedSubscriptionAsync(environment, environment.DedicatedTarget, nameof(FileFinalizedIntegrationEvent));
        Guid wrong = await SeedSubscriptionAsync(environment,
            new EventingDrainTarget(environment.DedicatedTenant.Id, null), nameof(FileFinalizedIntegrationEvent));

        using (environment.Drain.Begin(environment.DedicatedTarget))
        {
            using (var scope = environment.Provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IOutboxWriter>().AddAsync(integrationEvent);
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            }
            using (var scope = environment.Provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<EventingDbContext>().OutboxMessages
                    .Where(message => message.Id == integrationEvent.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(message => message.ProcessedOnUtc, (DateTime?)null));
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            }
        }

        recorder.Records.Count.ShouldBe(global ? 0 : 1);
        recorder.Records.Any(record => record.Subscription == wrong).ShouldBeFalse();
        if (!global) recorder.Records.Single().ShouldBe((environment.DedicatedTenant.Id, correct));
        string inboxConnection = global ? environment.DefaultConnection : environment.DedicatedConnection;
        string wrongConnection = global ? environment.DedicatedConnection : environment.DefaultConnection;
        string handler = typeof(WebhookFanoutHandler<FileFinalizedIntegrationEvent>).FullName!;
        (await ReadSnapshotAsync(inboxConnection, integrationEvent, handler)).InboxTenantRows.ShouldBe(1);
        (await ReadSnapshotAsync(wrongConnection, integrationEvent, handler)).InboxRows.ShouldBe(0);
        (await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent)).ProcessedRows.ShouldBe(1);
    }

    [Fact]
    public async Task DispatchAsync_Should_KeepRenewBillingInDefault_AndDeduplicateAfterSourceCompletionFailure()
    {
        var recorder = new WebhookRecorder();
        string defaultConnection = _factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
        await using RoutingEnvironment environment = await CreateEnvironmentAsync(services =>
        {
            AddWebhookServices(services, recorder);
            services.AddHeroDbContext<BillingDbContext>();
            services.AddScoped<IUsageReporter, UnusedUsageReporter>();
            services.AddScoped<IBillingService, BillingService>();
            services.AddScoped<IIntegrationEventHandler<TenantRenewedIntegrationEvent>, RenewMarkerHandler>();
            services.AddScoped<IIntegrationEventHandler<TenantRenewedIntegrationEvent>, TenantRenewedIntegrationEventHandler>();
        });
        var plan = BillingPlan.Create($"routing-{Guid.CreateVersion7():N}", "Routing", "USD", 10m);
        using (var scope = environment.Provider.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            billing.Plans.Add(plan);
            billing.Subscriptions.Add(Subscription.Create(environment.DedicatedTenant.Id, plan.Id,
                DateTime.UtcNow, environment.DedicatedTenant.ValidUpto));
            await billing.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Set<AppTenantInfo>()
                .Where(tenant => tenant.Id == environment.DedicatedTenant.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(tenant => tenant.Plan, plan.Key));
        }
        environment.PlanIds.Add(plan.Id);
        Guid subscription = await SeedSubscriptionAsync(environment, environment.DedicatedTarget, nameof(TenantRenewedIntegrationEvent));
        Guid wrongSubscription = await SeedSubscriptionAsync(environment,
            new EventingDrainTarget(environment.DedicatedTenant.Id, null), nameof(TenantRenewedIntegrationEvent));
        using HttpClient client = await new AuthHelper(_factory).CreateRootAdminClientAsync();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{environment.DedicatedTenant.Id}/renew",
            new { tenantId = environment.DedicatedTenant.Id, planKey = plan.Key });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        TenantRenewedIntegrationEvent integrationEvent;
        using (environment.Drain.Begin(environment.DefaultTarget))
        using (var scope = environment.Provider.CreateScope())
        {
            OutboxMessage message = await scope.ServiceProvider.GetRequiredService<EventingDbContext>().OutboxMessages
                .SingleAsync(message => message.TenantId == environment.DedicatedTenant.Id
                    && message.Type.Contains(nameof(TenantRenewedIntegrationEvent)));
            integrationEvent = (TenantRenewedIntegrationEvent)scope.ServiceProvider.GetRequiredService<IEventSerializer>()
                .Deserialize(message.Payload, message.Type)!;
            environment.Events.Add(integrationEvent);
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var failureStore = new CompletionFailingStore(outbox,
                scope.ServiceProvider.GetRequiredService<EventingDbContext>(), integrationEvent.Id, true);
            await CreateDispatcher(scope.ServiceProvider, failureStore).DispatchAsync();
        }

        string markerHandler = typeof(RenewMarkerHandler).FullName!;
        DatabaseSnapshot dedicated = await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent, markerHandler);
        DatabaseSnapshot shared = await ReadSnapshotAsync(defaultConnection, integrationEvent, markerHandler);
        dedicated.ConsumerRows.ShouldBe(1);
        dedicated.ConsumerTenantRows.ShouldBe(1);
        dedicated.InboxTenantRows.ShouldBe(1);
        dedicated.OutboxRows.ShouldBe(0);
        shared.ConsumerRows.ShouldBe(0);
        shared.InboxRows.ShouldBe(0);
        shared.OutboxRows.ShouldBe(1);
        shared.ProcessedRows.ShouldBe(0);
        shared.Retries.ShouldBe(1);
        shared.ClaimedRows.ShouldBe(0);
        string[] handlers = [markerHandler, typeof(TenantRenewedIntegrationEventHandler).FullName!,
            typeof(WebhookFanoutHandler<TenantRenewedIntegrationEvent>).FullName!];
        foreach (string handler in handlers)
        {
            (await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent, handler)).InboxTenantRows.ShouldBe(1);
            (await ReadSnapshotAsync(defaultConnection, integrationEvent, handler)).InboxRows.ShouldBe(0);
        }
        recorder.Records.ShouldHaveSingleItem().ShouldBe((environment.DedicatedTenant.Id, subscription));
        recorder.Records.Any(record => record.Subscription == wrongSubscription).ShouldBeFalse();
        using (var scope = environment.Provider.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            billing.Database.GetDbConnection().Database.ShouldBe(new NpgsqlConnectionStringBuilder(defaultConnection).Database);
            (await billing.Subscriptions.CountAsync(row => row.TenantId == environment.DedicatedTenant.Id)).ShouldBe(1);
            (await billing.Subscriptions.SingleAsync(row => row.TenantId == environment.DedicatedTenant.Id))
                .EndUtc.ShouldBe(integrationEvent.PeriodEndUtc);
            (await billing.Invoices.CountAsync(row => row.TenantId == environment.DedicatedTenant.Id)).ShouldBe(1);
        }
        await using (var connection = new NpgsqlConnection(environment.DedicatedConnection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT to_regclass('billing.\"Invoices\"') IS NULL", connection);
            (await command.ExecuteScalarAsync()).ShouldBe(true);
        }

        using (environment.Drain.Begin(environment.DefaultTarget))
        using (var scope = environment.Provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
            await context.OutboxMessages.Where(message => message.Id == integrationEvent.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(message => message.NextRetryAt, (DateTime?)null));
            var store = new CompletionFailingStore(scope.ServiceProvider.GetRequiredService<IOutboxStore>(),
                context, integrationEvent.Id, false);
            await CreateDispatcher(scope.ServiceProvider, store).DispatchAsync();
        }
        (await ReadSnapshotAsync(environment.DedicatedConnection, integrationEvent, markerHandler)).ShouldBe(dedicated);
        DatabaseSnapshot completed = await ReadSnapshotAsync(defaultConnection, integrationEvent, markerHandler);
        completed.ProcessedRows.ShouldBe(1);
        completed.Retries.ShouldBe(1);
        completed.ClaimedRows.ShouldBe(0);
        recorder.Records.Count.ShouldBe(1);
        using (var scope = environment.Provider.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            (await billing.Subscriptions.CountAsync(row => row.TenantId == environment.DedicatedTenant.Id)).ShouldBe(1);
            (await billing.Invoices.CountAsync(row => row.TenantId == environment.DedicatedTenant.Id)).ShouldBe(1);
        }
    }

    private static void AddWebhookServices(IServiceCollection services, WebhookRecorder recorder)
    {
        services.AddHeroDbContext<WebhookDbContext>();
        services.AddSingleton<IWebhookDispatcher>(recorder);
        services.AddScoped(typeof(IIntegrationEventHandler<>), typeof(WebhookFanoutHandler<>));
    }

    private static async Task<Guid> SeedSubscriptionAsync(RoutingEnvironment environment, EventingDrainTarget target, string eventType)
    {
        using (environment.Drain.Begin(target))
        using (var scope = environment.Provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
            var subscription = WebhookSubscription.Create("https://routing.example.test/hook", [eventType], "test-hash");
            context.Subscriptions.Add(subscription);
            await context.SaveChangesAsync();
            if (target.ConnectionString is null) environment.SubscriptionIds.Add(subscription.Id);
            return subscription.Id;
        }
    }

    private async Task<RoutingEnvironment> CreateEnvironmentAsync(Action<IServiceCollection>? configure = null)
    {
        string connection = _factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
        string suffix = Guid.CreateVersion7().ToString("N");
        string database = $"routing_{suffix}";
        string dedicatedConnection = new NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;
        ServiceProvider provider = BuildProvider(connection, configure);
        var environment = new RoutingEnvironment(provider, connection, dedicatedConnection, database,
            new AppTenantInfo($"dedicated-{suffix}", $"dedicated-{suffix}", dedicatedConnection, "routing@example.test"),
            new AppTenantInfo($"shared-{suffix}", $"shared-{suffix}", dedicatedConnection, "routing@example.test"),
            new AppTenantInfo($"default-{suffix}", $"default-{suffix}", null, "routing@example.test"));
        try
        {
            await ExecuteDatabaseCommandAsync(connection, $"CREATE DATABASE {QuoteIdentifier(database)}");
            environment.DatabaseCreated = true;
            using (environment.Drain.Begin(environment.DedicatedTarget))
            using (var scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<EventingDbContext>().Database.MigrateAsync();
                await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
                if (scope.ServiceProvider.GetService<WebhookDbContext>() is { } webhooks)
                    await webhooks.Database.MigrateAsync();
            }
            using (var scope = provider.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
                (await store.AddAsync(environment.DedicatedTenant)).ShouldBeTrue();
                (await store.AddAsync(environment.SharedTenant)).ShouldBeTrue();
                (await store.AddAsync(environment.DefaultTenant)).ShouldBeTrue();
            }
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    private ServiceProvider BuildProvider(string defaultConnection, Action<IServiceCollection>? configure = null)
        => CreateProvider(_factory.Services, defaultConnection, configure);

    internal static ServiceProvider CreateProvider(IServiceProvider hostServices, string defaultConnection,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EventingOptions:Provider"] = "InMemory",
            ["EventingOptions:UseHostedServiceDispatcher"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(hostServices.GetRequiredService<IHostEnvironment>());
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
        services.AddScoped<IEventTenantResolver, EventTenantResolver>();
        services.AddMultiTenant<AppTenantInfo>()
            .WithStore<EFCoreStore<TenantDbContext, AppTenantInfo>>(ServiceLifetime.Scoped);
        services.AddEventingCore(configuration);
        services.AddHeroDbContext<CatalogDbContext>();
        services.Replace(ServiceDescriptor.Singleton<IEventTenantScope, FinbuckleEventTenantScope>());
        services.Replace(ServiceDescriptor.Singleton<IEventingDrainScope, FinbuckleEventingDrainScope>());
        services.Replace(ServiceDescriptor.Scoped<IEventingDrainTargetProvider, TenantStoreDrainTargetProvider>());
        services.AddScoped<IIntegrationEventHandler<FileFinalizedIntegrationEvent>, FileEventMarkerHandler>();
        configure?.Invoke(services);
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

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(string connectionString, IIntegrationEvent integrationEvent, string? handlerName = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM catalog."Brands" WHERE "Name" = @marker),
                (SELECT COUNT(*) FROM catalog."Brands" WHERE "Name" = @marker AND "TenantId" = @tenant),
                (SELECT COUNT(*) FROM framework."InboxMessages" WHERE "Id" = @event AND "HandlerName" = @handler),
                (SELECT COUNT(*) FROM framework."InboxMessages" WHERE "Id" = @event AND "HandlerName" = @handler AND "TenantId" IS NOT DISTINCT FROM @inboxTenant),
                (SELECT COUNT(*) FROM framework."OutboxMessages" WHERE "Id" = @event),
                (SELECT COUNT(*) FROM framework."OutboxMessages" WHERE "Id" = @event AND "ProcessedOnUtc" IS NOT NULL),
                (SELECT COUNT(*) FROM framework."OutboxMessages" WHERE "Id" = @event AND "ClaimedUntilUtc" IS NOT NULL),
                (SELECT COALESCE(SUM("RetryCount"), 0) FROM framework."OutboxMessages" WHERE "Id" = @event)
            """, connection);
        command.Parameters.AddWithValue("marker", $"dispatch-{integrationEvent.Id:N}");
        command.Parameters.AddWithValue("tenant", integrationEvent.TenantId ?? MultitenancyConstants.Root.Id);
        command.Parameters.AddWithValue("inboxTenant", NpgsqlTypes.NpgsqlDbType.Text, (object?)integrationEvent.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("event", integrationEvent.Id);
        command.Parameters.AddWithValue("handler", handlerName ?? typeof(FileEventMarkerHandler).FullName!);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new DatabaseSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private static async Task CleanupDefaultRowsAsync(string connectionString, Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM catalog."Brands" WHERE "Name" = @marker;
            DELETE FROM framework."InboxMessages" WHERE "Id" = @event;
            DELETE FROM framework."OutboxMessages" WHERE "Id" = @event;
            """, connection);
        command.Parameters.AddWithValue("marker", $"dispatch-{eventId:N}");
        command.Parameters.AddWithValue("event", eventId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record DatabaseSnapshot(long ConsumerRows, long ConsumerTenantRows, long InboxRows,
        long InboxTenantRows, long OutboxRows, long ProcessedRows, long ClaimedRows, long Retries);

    private static FileFinalizedIntegrationEvent NewFileEvent(string? tenantId)
    {
        Guid id = Guid.CreateVersion7();
        return new(id, DateTime.UtcNow, tenantId, $"corr-{id:N}", "Files", Guid.CreateVersion7(), "Test", null,
            "application/octet-stream", 1, 1);
    }

    private sealed class RoutingEnvironment(
        ServiceProvider provider, string defaultConnection, string dedicatedConnection, string database,
        AppTenantInfo dedicatedTenant, AppTenantInfo sharedTenant, AppTenantInfo defaultTenant) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public string DefaultConnection { get; } = defaultConnection;
        public string DedicatedConnection { get; } = dedicatedConnection;
        public AppTenantInfo DedicatedTenant { get; } = dedicatedTenant;
        public AppTenantInfo SharedTenant { get; } = sharedTenant;
        public AppTenantInfo DefaultTenant { get; } = defaultTenant;
        public bool DatabaseCreated { get; set; }
        public List<IIntegrationEvent> Events { get; } = [];
        public List<Guid> SubscriptionIds { get; } = [];
        public List<Guid> PlanIds { get; } = [];
        public IMultiTenantContextAccessor<AppTenantInfo> Accessor => Provider.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
        public IEventingDrainScope Drain => Provider.GetRequiredService<IEventingDrainScope>();
        public EventingDrainTarget DefaultTarget { get; } = new(MultitenancyConstants.Root.Id, null);
        public EventingDrainTarget DedicatedTarget => new(DedicatedTenant.Id, DedicatedConnection);

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cleanupTenant = Drain.Begin(DefaultTarget);
                using var scope = Provider.CreateScope();
                string[] tenants = [DedicatedTenant.Id, SharedTenant.Id, DefaultTenant.Id];
                await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Set<AppTenantInfo>()
                    .Where(tenant => tenants.Contains(tenant.Id)).ExecuteDeleteAsync();
                if (scope.ServiceProvider.GetService<WebhookDbContext>() is { } webhooks)
                    await webhooks.Subscriptions.IgnoreQueryFilters()
                        .Where(subscription => SubscriptionIds.Contains(subscription.Id)).ExecuteDeleteAsync();
                if (scope.ServiceProvider.GetService<BillingDbContext>() is { } billing)
                {
                    await billing.Invoices.Where(invoice => tenants.Contains(invoice.TenantId)).ExecuteDeleteAsync();
                    await billing.Subscriptions.Where(subscription => tenants.Contains(subscription.TenantId)).ExecuteDeleteAsync();
                    await billing.Plans.Where(plan => PlanIds.Contains(plan.Id)).ExecuteDeleteAsync();
                }
                foreach (IIntegrationEvent integrationEvent in Events)
                    await CleanupDefaultRowsAsync(DefaultConnection, integrationEvent.Id);
            }
            finally
            {
                await Provider.DisposeAsync();
                if (DatabaseCreated)
                    await ExecuteDatabaseCommandAsync(DefaultConnection, $"DROP DATABASE {QuoteIdentifier(database)} WITH (FORCE)");
            }
        }
    }

    private sealed class PendingTenantResolver(
        IEventTenantResolver inner, IMultiTenantContextAccessor<AppTenantInfo> accessor, Task lookup) : IEventTenantResolver
    {
        public async Task<AppTenantInfo?> ResolveAsync(string tenantId, CancellationToken ct = default)
        {
            accessor.MultiTenantContext.TenantInfo!.Id.ShouldBe(MultitenancyConstants.Root.Id);
            accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBeEmpty();
            await lookup.WaitAsync(ct).ConfigureAwait(false);
            accessor.MultiTenantContext.TenantInfo.Id.ShouldBe(MultitenancyConstants.Root.Id);
            accessor.MultiTenantContext.TenantInfo.ConnectionString.ShouldBeEmpty();
            return await inner.ResolveAsync(tenantId, ct).ConfigureAwait(false);
        }
    }

    private sealed class ObservingFileHandler(FileEventMarkerHandler inner, Action observe)
        : IIntegrationEventHandler<FileFinalizedIntegrationEvent>
    {
        public async Task HandleAsync(FileFinalizedIntegrationEvent @event, CancellationToken ct = default)
        {
            observe();
            await Task.Yield();
            observe();
            await inner.HandleAsync(@event, ct).ConfigureAwait(false);
        }
    }

    public sealed class FileEventMarkerHandler(CatalogDbContext context) : IIntegrationEventHandler<FileFinalizedIntegrationEvent>
    {
        public async Task HandleAsync(FileFinalizedIntegrationEvent integrationEvent, CancellationToken ct = default)
        {
            context.Brands.Add(Brand.Create($"dispatch-{integrationEvent.Id:N}", null, null));
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public sealed class RenewMarkerHandler(CatalogDbContext context) : IIntegrationEventHandler<TenantRenewedIntegrationEvent>
    {
        public async Task HandleAsync(TenantRenewedIntegrationEvent integrationEvent, CancellationToken ct = default)
        {
            context.Brands.Add(Brand.Create($"dispatch-{integrationEvent.Id:N}", null, null));
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public sealed class FailingFileHandler : IIntegrationEventHandler<FileFinalizedIntegrationEvent>
    {
        public Task HandleAsync(FileFinalizedIntegrationEvent @event, CancellationToken ct = default)
            => throw new InvalidOperationException("Injected consumer failure.");
    }

    private sealed class WebhookRecorder : IWebhookDispatcher
    {
        public ConcurrentQueue<(string Tenant, Guid Subscription)> Records { get; } = new();

        public Task EnqueueAsync(string tenantId, Guid subscriptionId, string eventType, string payloadJson, CancellationToken cancellationToken = default)
        {
            Records.Enqueue((tenantId, subscriptionId));
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedUsageReporter : IUsageReporter
    {
        public Task<IReadOnlyList<UsageSnapshot>> CaptureForPeriodAsync(string tenantId, int periodYear, int periodMonth,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Renew does not capture metered usage.");
    }

    private static OutboxDispatcher CreateDispatcher(IServiceProvider provider, IOutboxStore store)
        => new(store, provider.GetRequiredService<IEventBus>(), provider.GetRequiredService<IEventSerializer>(),
            provider.GetRequiredService<IOptions<EventingOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>>());

    private sealed class CompletionFailingStore(IOutboxStore inner, EventingDbContext context, Guid eventId, bool failCompletion) : IOutboxStore
    {
        public Task AddAsync(IIntegrationEvent @event, CancellationToken ct = default) => inner.AddAsync(@event, ct);
        public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int batchSize, string claimedBy, TimeSpan lease, CancellationToken ct = default)
        {
            IReadOnlyList<OutboxMessage> messages = await inner.ClaimBatchAsync(batchSize, claimedBy, lease, ct).ConfigureAwait(false);
            Guid[] unrelated = messages.Where(message => message.Id != eventId).Select(message => message.Id).ToArray();
            if (unrelated.Length != 0)
                await context.OutboxMessages.Where(message => unrelated.Contains(message.Id) && message.ClaimedBy == claimedBy)
                    .ExecuteUpdateAsync(update => update.SetProperty(message => message.ClaimedBy, (string?)null)
                        .SetProperty(message => message.ClaimedUntilUtc, (DateTime?)null), ct).ConfigureAwait(false);
            return messages.Where(message => message.Id == eventId).ToArray();
        }
        public Task MarkAsProcessedAsync(OutboxMessage message, CancellationToken ct = default)
            => failCompletion && message.Id == eventId ? throw new InvalidOperationException("Injected source completion failure.") : inner.MarkAsProcessedAsync(message, ct);
        public Task MarkAsFailedAsync(OutboxMessage message, string error, bool isDead, CancellationToken ct = default)
            => inner.MarkAsFailedAsync(message, error, isDead, ct);
        public Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int max, CancellationToken ct = default) => inner.GetDeadLetteredAsync(max, ct);
        public Task<int> RedriveDeadLettersAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default) => inner.RedriveDeadLettersAsync(ids, ct);
    }
}
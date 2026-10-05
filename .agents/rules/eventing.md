# Eventing — domain events, integration events, Outbox/Inbox

Read before publishing/handling cross-module events. `src/BuildingBlocks/Eventing/`.

## Two tiers

- **Domain events** (in-process, pre-commit) — inherit `DomainEvent` (record: `EventId`, `OccurredOnUtc`, `CorrelationId`, `TenantId`). Raised on aggregates (`IHasDomainEvents`).
- **Integration events** (cross-module, async) — implement `IIntegrationEvent` (`Id`, `OccurredOnUtc`, `TenantId`, `CorrelationId`, `Source`). Handlers implement `IIntegrationEventHandler<T>` (single `HandleAsync(T, ct)`), are `sealed`, live in `Events/` or `IntegrationEventHandlers/`.

## The Outbox is the only way to publish

**Do not call `IEventBus` directly from a handler.** Publish via the outbox so the event commits with the business write and survives a crash:

```csharp
await _outbox.AddAsync(integrationEvent, ct).ConfigureAwait(false);   // IOutboxWriter
```

Inject **`IOutboxWriter`** (`FSH.Framework.Eventing.Abstractions`) — the publish-side contract, and all a module ever needs. `IOutboxStore` is the full dispatcher-side surface and lives in the eventing runtime, which modules don't reference.

`EfCoreOutboxStore.AddAsync` serializes + `SaveChanges` immediately, joining the caller's transaction when there is one. `OutboxDispatcherHostedService` polls every `OutboxDispatchIntervalSeconds` (default 10), `OutboxDispatcher` **claims** a batch (`OutboxBatchSize`, default 100), publishes via `IEventBus`, and dead-letters after `OutboxMaxRetries` (default 5) → `IsDead`. Failures back off exponentially (`NextRetryAt`); `RedriveDeadLettersAsync` recovers dead rows. `OutboxMessage`/`InboxMessage` are `IGlobalEntity` (no tenant filter — the dispatcher has no tenant context; `TenantId` is an explicit column).

**Publishing is asynchronous.** The consumer runs on the next dispatch cycle, not inside the request. Don't write a caller — or a test — that assumes the side effect already happened. Integration tests drain explicitly via `OutboxDrain.DrainAsync`.

**The one documented exception:** Chat `SendMessageCommandHandler` still publishes mentions on the bus, because the Notifications handler pushes over SignalR and a delayed mention badge reads as broken. Adding a second exception needs the same kind of reason, in a comment at the call site.

## One store, owned by the framework

`OutboxMessages`/`InboxMessages` live in schema `framework`, owned by `EventingDbContext` (`src/BuildingBlocks/Eventing/Persistence/`) — **not** by any module's context. That is what keeps `IOutboxStore`/`IInboxStore` to a single, non-keyed DI registration: registering them per module DbContext made .NET DI resolve whichever module registered last for the whole application, so a second module publishing broke every module's outbox (issue #1349). `EventingRegistrationTests` guards the registration count; don't add a second one.

`EventingDbContext` derives from `BaseDbContext`, so a tenant with a dedicated database gets its outbox rows in that database, next to the business data they accompany.

## Dispatch across tenant databases

Because rows follow the tenant connection, the dispatcher can't just poll one database. Each cycle it asks `IEventingDrainTargetProvider` for the drain targets and runs one pass per target inside `IEventingDrainScope`, which installs the tenant context **before** the scope's `EventingDbContext` is built (it captures `TenantInfo`, and with it the connection string, at construction).

Defaults in BuildingBlocks are single-database (`SingleDatabaseDrainTargetProvider`, `NullEventingDrainScope`); the multitenancy module replaces them with `TenantStoreDrainTargetProvider` (default DB + one target per distinct **active** per-tenant connection string; tenants sharing a database collapse to one target) and `FinbuckleEventingDrainScope`. One unreachable tenant database is logged and skipped, not fatal to the cycle.

Source and consumer routing are separate. A default drain explicitly installs root with an empty connection string and restores the previous context. `InMemoryEventBus` calls `IEventTenantScope.ExecuteAsync` before constructing its consumer scope. The Finbuckle implementation resolves full tenant metadata from the default catalog through a short-lived scoped `EventTenantResolver`, then invokes the dispatch callback in the same async flow. Never return an asynchronously installed AsyncLocal context to a caller that constructs consumers afterwards.

Global events execute in root/default; their Inbox retains the event's null `TenantId`. Unknown/deleted and inactive tenants fail before consumer construction; the original source Outbox retries and eventually dead-letters. Dedicated inactive databases are excluded from automatic drain targets, so their pending rows require operator review/reactivation; dead letters need explicit redrive. Do not silently send rejected events to default.

The synchronous `Begin` and two-argument constructor remain compatibility bridges, not asynchronous catalog resolution APIs. `Begin(null/whitespace)` leaves context untouched, while `ExecuteAsync(null/whitespace, ...)` selects default. Matching synchronous scopes retain full ambient metadata and restore it on disposal; different IDs retain legacy identity-only behavior. Use `ExecuteAsync` for event dispatch. The interface's default method keeps existing synchronous/no-op implementations source-compatible.

Central Billing business data remains in default even while its consumer runs for a dedicated tenant; its framework Inbox follows the event tenant. Consumer success/Inbox and source completion are separate commits. Redelivery after source completion failure is suppressed by the destination Inbox, but a failure between a business write and Inbox insertion is not guaranteed exactly-once. No distributed transaction or automatic repair of previously misrouted data is introduced. External docs/changelog and deployment/data-recovery guidance remain tracked by the finish issue.

## Multi-instance safety

`ClaimBatchAsync` leases rows with `FOR UPDATE SKIP LOCKED` in a single `UPDATE … RETURNING`, so several API instances partition a batch instead of all publishing the same message. `ClaimedUntilUtc` is an expiry (`OutboxClaimLeaseSeconds`, default 300), so a dispatcher that dies mid-batch has its rows recovered rather than stranded — raise it if a batch can take longer than the lease, or a second instance re-claims rows still in flight. Completing or failing a message releases the lease. Non-Postgres providers have no portable `SKIP LOCKED`: they fall back to an unclaimed read and log a warning that only one instance is safe.

## Atomicity

`IScopedDbConnectionProvider` gives every DbContext in a DI scope the same `DbConnection`, which is the only way EF Core can enlist a second context in a transaction another one opened (Npgsql has no distributed-transaction promotion). `AmbientDbTransactionRegistry` — an `IDbTransactionInterceptor` on every Hero context — records open transactions, since `DbConnection` can't be asked. `AddAsync` joins the ambient transaction when there is one, so the outbox row commits or rolls back with the business data; with none, it commits on its own exactly as before.

## Idempotency is free (in-memory bus)

`InMemoryEventBus` resolves handlers in a fresh DI scope and applies the **Inbox**: skips if `IInboxStore.HasProcessedAsync(eventId, handlerName)`, marks processed after success. Composite key `{Id, HandlerName}`; concurrent-insert race is swallowed. Don't hand-roll dedup.

## Wiring

The **host** bootstraps eventing once (`FSH.Starter.Api/Program.cs` and `FSH.Starter.DbMigrator/Program.cs`, before `AddModules` so `EventingDbInitializer` migrates the `framework` schema first):

```csharp
builder.Services.AddEventingCore(builder.Configuration);   // serializer + bus + dispatcher + EventingDbContext + stores
```

A **module** only registers its handlers:

```csharp
services.AddIntegrationEventHandlers(typeof(MyModule).Assembly);        // scans IIntegrationEventHandler<>
```

There is no per-module store registration — `AddEventingForDbContext<T>` was removed in #1349. A module publishes by injecting `IOutboxWriter`; nothing else is needed.

Bus = `EventingOptions.Provider`: `"RabbitMQ"` → `RabbitMqEventBus` (durable topic exchange); else `InMemoryEventBus` (default).

## Gotchas

- **Renaming/moving an integration event type breaks deserialization** — the outbox stores the assembly-qualified type name; `Type.GetType()` returns null → the message dead-letters. Keep event type names/namespaces stable, or migrate dead rows.
- **Background handlers carry no HTTP context.** The in-memory bus resolves the full event tenant before constructing handlers/Inbox. `WebhookFanoutHandler` uses that context without ID-only replacement; standalone jobs must establish full tenant context before constructing their DbContexts (see `modules/webhooks.md`).
- The in-memory bus awaits handlers in a fresh consumer DI scope within the publisher's dispatch flow. Keep work minimal; exceptions surface to the caller. Via Outbox, failures update the original source database rather than the consumer's database.
- Set `UseHostedServiceDispatcher=false` to drive the outbox via Hangfire instead of the hosted service.
- A background publisher must set the tenant context **before** `AddAsync` — otherwise, with per-tenant databases, the row lands in the wrong one (see `TenantExpiryScanJob`).

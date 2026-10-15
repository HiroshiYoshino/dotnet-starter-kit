# Module: Webhooks

Tenant-scoped outbound webhook subscriptions with HMAC-signed delivery and retries. Module `Order = 400`.

**Entities / DbContext:** `WebhookSubscription` (`Url`, `EventsCsv`, `SecretHash`, `IsActive`), `WebhookDelivery` (per-attempt log). `WebhookDbContext` (tenant-filtered). Contracts expose **DTOs only** — `IWebhookDispatcher`/`IWebhookDeliveryService` are internal.
**Areas:** Create/Delete/Get subscriptions, GetDeliveries, Test. Full list: `Features/v1/` or `/scalar`.

## Gotchas

- **Fan-out is an open-generic handler** — `WebhookFanoutHandler<TEvent>` is registered as an open generic, so it handles **every** `IIntegrationEvent` with no per-event wiring. It skips events with null `TenantId` (subscriptions are tenant-only) and matches event-type name against each subscription's `EventsCsv` (`*` wildcard supported).
- **Fan-out consumes the bus-resolved tenant** - `IEventTenantScope.ExecuteAsync` resolves full catalog metadata before handler/DbContext construction. Fan-out must not replace it with ID-only information; a mismatched ambient ID fails before querying subscriptions. Direct callers must satisfy the same construction precondition. Standalone `WebhookDispatchJob` establishes its own context; job routing and external HTTP delivery are separate from successful fan-out/Inbox recording. See `eventing.md`, `jobs.md`.
- **HMAC signing** — `X-Webhook-Signature: sha256=<hex HMACSHA256>` (`WebhookPayloadSigner.Sign`), plus `X-Webhook-Event` and `X-Webhook-Delivery-Id` headers.
- **Delivery** — `WebhookDispatcher.EnqueueAsync` enqueues a Hangfire `WebhookDispatchJob` per subscription; `[AutomaticRetry(Attempts=4, DelaysInSeconds={30,120,600,3600})]`. Transient (5xx/408/429) throws to reschedule; permanent 4xx completes silently. Each attempt persists a `WebhookDelivery` row. The `"Webhooks"` HttpClient uses `AddHeroResilience` (see `resilience.md`).

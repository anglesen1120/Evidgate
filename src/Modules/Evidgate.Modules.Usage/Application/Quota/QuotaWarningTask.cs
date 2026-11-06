using System.Text.Json;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// The payload of the first-warning task: which counter crossed its warning line, for which scope and
/// month, and the numbers that made it cross. Ids and numbers only — the notification's wording and its
/// recipients are the consumer's, never the metering path's.
/// </summary>
/// <param name="CounterId">The <c>quota_counter</c> row that stamped <c>warned_at</c>.</param>
/// <param name="ProjectId">The project scope, or null for a tenant-level counter.</param>
/// <param name="Period">The billing month, <c>YYYY-MM</c>.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="Used">The usage the counter holds at the crossing.</param>
/// <param name="QuotaLimit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit the crossing reached.</param>
internal sealed record QuotaWarningPayload(
    Guid CounterId,
    Guid? ProjectId,
    string Period,
    string Metric,
    decimal Used,
    decimal QuotaLimit,
    decimal WarnRatio);

/// <summary>
/// The task the quota gate emits once per counter per period, when its own statement moves
/// <c>warned_at</c> from null to a stamp. It is written through the outbox inside the metering
/// transaction, so the counter change and the notification commit or roll back together.
/// <para>
/// No handler serves this type in this build: delivery (the notification channel and its audience) is a
/// later story's, and a no-op handler would report a warning as delivered. Until then a dispatched row
/// waits unclaimed in the task queue, which is the visible backlog that story drains.
/// </para>
/// </summary>
internal static class QuotaWarningTask
{
    /// <summary>The handler key the notification consumer will register.</summary>
    internal const string TaskType = "usage.quota.warn";

    /// <summary>The aggregate a warning row belongs to.</summary>
    internal const string AggregateType = "quota_counter";

    /// <summary>The wire payload, snake_case like every other outbox payload.</summary>
    /// <param name="payload">The crossing to announce.</param>
    internal static string PayloadJson(QuotaWarningPayload payload) => JsonSerializer.Serialize(new
    {
        counter_id = payload.CounterId,
        project_id = payload.ProjectId,
        period = payload.Period,
        metric = payload.Metric,
        used = payload.Used,
        quota_limit = payload.QuotaLimit,
        warn_ratio = payload.WarnRatio,
    });

    /// <summary>
    /// The dedupe key: one warning per counter. A counter is one scope in one month, and its warning
    /// transition happens once, so the key cannot collide for a producer that flushes the same command again.
    /// </summary>
    /// <param name="counterId">The counter that warned.</param>
    internal static string IdempotencyKey(Guid counterId) => $"usage:quota:warn:{counterId:D}";
}

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Why a row reached `dead_letter` (01.5.1 §6/§7). The two causes need different
/// operator reactions — a poison payload versus a consumer that died holding the
/// row — so the alert carries the distinction instead of making the receiver infer
/// it from `last_error`.
/// </summary>
public enum TaskDeadLetterSource
{
    /// <summary>A handler threw and the attempt was already the last one (§6 `active → dead_letter`).</summary>
    Consumer,

    /// <summary>The consumer never finished: the lease ran out with every attempt used (§6 visibility timeout).</summary>
    ExpiredLease,
}

/// <summary>
/// One `dead_letter` transition (01.5.1 §7 "Alert hook"). <see cref="LastError"/>
/// is the value the row carries — already redacted and truncated by
/// <see cref="TaskErrorText"/> (00 §5.7 S3), never a raw exception message.
/// </summary>
/// <param name="TaskId">Row that was dead-lettered.</param>
/// <param name="Queue">Queue it came from.</param>
/// <param name="TaskType">Handler key of the row.</param>
/// <param name="OrgId">Tenant of the row, or null for a platform task.</param>
/// <param name="Attempts">Attempts the row spent before giving up.</param>
/// <param name="MaxAttempts">The cap those attempts reached.</param>
/// <param name="LastError">Redacted error stored on the row, or null when the transition wrote none.</param>
/// <param name="Source">Which path dead-lettered the row.</param>
public sealed record TaskDeadLetterAlert(
    Guid TaskId,
    string Queue,
    string TaskType,
    Guid? OrgId,
    int Attempts,
    int MaxAttempts,
    string? LastError,
    TaskDeadLetterSource Source);

/// <summary>
/// The alert hook of 01.5.1 §7: notified once per `dead_letter` transition, by
/// both the consumer's retry-cap path and the claimer's expired-lease path.
/// 10.3.1-T5 and the 00 §11 SLIs consume it.
/// </summary>
/// <remarks>
/// <para>
/// An implementation MUST NOT throw and MUST NOT block the consumer loop: the
/// transition it is told about has already been committed, and an alert channel
/// that is down (a webhook, a pager API, a metrics backend) must never turn a
/// dead-lettered row into a stalled queue. The mechanism still guards the call
/// (<c>TaskAlertNotifier.NotifySafelyAsync</c>), because a third-party sink cannot
/// be trusted to honour that on its own.
/// </para>
/// <para>
/// Delivery is at-most-once and best-effort by design: the row is the record, the
/// alert is the notification. Nothing here is a durable outbox, and a lost alert
/// is recovered from `GET /internal/operator/tasks/dead-letter` (§5) — a captured
/// sink that comes back up does not replay it.
/// </para>
/// </remarks>
public interface ITaskAlertSink
{
    /// <summary>Reports one `dead_letter` transition.</summary>
    /// <param name="alert">The transition that was committed.</param>
    /// <param name="cancellationToken">Bounded by the caller: a hang here must not outlive the transition's own budget.</param>
    ValueTask NotifyAsync(TaskDeadLetterAlert alert, CancellationToken cancellationToken = default);
}

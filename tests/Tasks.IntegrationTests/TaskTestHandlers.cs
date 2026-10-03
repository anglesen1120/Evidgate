using System.Collections.Concurrent;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Tasks.IntegrationTests;

/// <summary>
/// The handler keys the suite's own task handlers claim. They are permanent test
/// doubles, not scratch classes: every consumer test needs a task_type with a
/// handler this process knows, one it does not (<see cref="Unhandled"/>), and one
/// that reads through the tenant scope.
/// </summary>
internal static class TaskHandlerTypes
{
    internal const string Recording = "test.record";
    internal const string Tenant = "test.tenant";

    /// <summary>A type no handler in this process claims (01.5.1 §11: it must stay queued).</summary>
    internal const string Unhandled = "test.unhandled";
}

/// <summary>Payload of <see cref="RecordingTaskHandler"/>.</summary>
public sealed record RecordingPayload(string Value);

/// <summary>Payload of <see cref="TenantTaskHandler"/>.</summary>
public sealed record TenantPayload(string Marker);

/// <summary>
/// What the suite's handlers observed, shared between the test and the handler: how
/// often each payload ran (twice for one marker is the double-execution failure AC7
/// forbids) and how many times the tenant handler ran in total.
/// </summary>
public sealed class TaskRunRecorder
{
    private readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.Ordinal);
    private int _tenantRuns;

    /// <summary>Message of the poison-pill failure; also asserted in <c>last_error</c>.</summary>
    internal const string PoisonMessage = "poison payload";

    /// <summary>When set, <see cref="RecordingTaskHandler"/> throws it instead of succeeding.</summary>
    internal Exception? Failure { get; set; }

    /// <summary>How long <see cref="RecordingTaskHandler"/> takes, so a batch can outlive its lease.</summary>
    internal TimeSpan Delay { get; set; }

    /// <summary>Runs per payload value, as seen by the handler.</summary>
    internal IReadOnlyDictionary<string, int> Runs => _runs;

    /// <summary>Total invocations of the tenant handler.</summary>
    internal int TenantRuns => Volatile.Read(ref _tenantRuns);

    /// <summary>Total invocations across payloads, as seen by the handler.</summary>
    internal int TotalRuns => _runs.Values.Sum();

    internal void RecordRun(string value) => _runs.AddOrUpdate(value, 1, (_, count) => count + 1);

    internal void RecordTenantRun() => Interlocked.Increment(ref _tenantRuns);
}

/// <summary>
/// A handler that records every run and can be turned into a poison pill, so one
/// handler covers "ran and completed", "ran exactly once under concurrency" and
/// "retried, then dead-lettered".
/// </summary>
public sealed class RecordingTaskHandler : ITaskHandler<RecordingPayload>
{
    private readonly TaskRunRecorder _recorder;

    public RecordingTaskHandler(TaskRunRecorder recorder) => _recorder = recorder;

    /// <inheritdoc />
    public string TaskType => TaskHandlerTypes.Recording;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public ValueTask HandleAsync(RecordingPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        _recorder.RecordRun(payload.Value);

        return HandleAsyncCore(payload, cancellationToken);
    }

    private async ValueTask HandleAsyncCore(RecordingPayload payload, CancellationToken cancellationToken)
    {
        if (_recorder.Delay > TimeSpan.Zero)
        {
            await Task.Delay(_recorder.Delay, cancellationToken);
        }

        if (_recorder.Failure is not null)
        {
            throw _recorder.Failure;
        }
    }
}

/// <summary>
/// The tenant handler of 01.5.1 §9 <c>Handler_OpensTenantScopeFromRow</c>: it reads
/// through the scope the consumer opened from the row's <c>org_id</c>, so the count
/// it sees proves both that the scope came from the row and that RLS narrowed it to
/// that org — and it writes that count under its own org, where the test reads it
/// back with the owner connection.
/// </summary>
public sealed class TenantTaskHandler : ITaskHandler<TenantPayload>
{
    private readonly ITenantDb _db;
    private readonly TaskRunRecorder _recorder;

    public TenantTaskHandler(ITenantDb db, TaskRunRecorder recorder)
    {
        _db = db;
        _recorder = recorder;
    }

    /// <inheritdoc />
    public string TaskType => TaskHandlerTypes.Tenant;

    /// <inheritdoc />
    public async ValueTask HandleAsync(TenantPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        _recorder.RecordTenantRun();

        var seen = await _db.QuerySingleOrDefaultAsync<int>(
            "select count(*)::int from public.rls_probe",
            cancellationToken: cancellationToken);

        await _db.ExecuteAsync(
            "insert into public.rls_probe (org_id, id, label) values (evidgate.current_org_id(), @id, @label)",
            new { id = Guid.CreateVersion7(), label = $"{payload.Marker}:seen={seen}" },
            cancellationToken);
    }
}

/// <summary>
/// The recording <see cref="ITaskAlertSink"/> of the dead-letter tests (01.5.1
/// §7): captures every alert the consumer and the claimer hand it, so a test can
/// assert both the consumer-path and the expired-lease-path notification — and
/// that a retry (not yet dead-lettered) sends nothing.
/// </summary>
public sealed class RecordingTaskAlertSink : ITaskAlertSink
{
    private readonly ConcurrentQueue<TaskDeadLetterAlert> _alerts = new();

    /// <summary>Every alert delivered, in delivery order.</summary>
    public IReadOnlyCollection<TaskDeadLetterAlert> Alerts => _alerts.ToArray();

    /// <inheritdoc />
    public ValueTask NotifyAsync(TaskDeadLetterAlert alert, CancellationToken cancellationToken = default)
    {
        _alerts.Enqueue(alert);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A sink that always throws — the proof that the mechanism's guard keeps a
/// broken alert channel from breaking the consumer loop (01.5.1 §7, the
/// ITaskAlertSink "MUST NOT throw" contract).
/// </summary>
public sealed class ThrowingTaskAlertSink : ITaskAlertSink
{
    /// <inheritdoc />
    public ValueTask NotifyAsync(TaskDeadLetterAlert alert, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("the alert sink is down");
}

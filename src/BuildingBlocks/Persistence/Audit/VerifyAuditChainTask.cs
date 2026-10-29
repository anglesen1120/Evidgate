using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>Payload of the <c>audit.verify_chain</c> task.</summary>
/// <param name="OrgBatch">Org ids read per page.</param>
public sealed record VerifyAuditChainPayload(int OrgBatch);

/// <summary>Names and schedule of the chain-verification task (02.7.1-T3.2).</summary>
public static class AuditSweeps
{
    /// <summary>Task type and schedule name.</summary>
    public const string VerifyChainTaskType = "audit.verify_chain";

    /// <summary>Cronos cadence: every six hours at minute 7 (⚠️ cadence, 02.7.1 §5; off the hour so it never queues with the other sweeps).</summary>
    public const string VerifyChainCron = "7 */6 * * *";

    /// <summary>The queue the task runs on.</summary>
    public const string Queue = EvidgateSweeps.MaintenanceQueue;

    /// <summary>Orgs listed per page.</summary>
    public const int OrgBatch = 100;

    /// <summary>The schedule definition (queue <c>maintenance</c>, unique <c>(schedule_name, fire_at)</c> per fire).</summary>
    public static ScheduleDefinition VerifyChain { get; } = new(
        name: VerifyChainTaskType,
        cron: VerifyChainCron,
        queue: Queue,
        taskType: VerifyChainTaskType,
        payloadFactory: _ => TaskPayloadSerializer.Serialize(new VerifyAuditChainPayload(OrgBatch)));
}

/// <summary>Raised when a chain is broken so the task retries and then dead-letters, visibly (AC6).</summary>
public sealed class AuditChainBrokenException(IReadOnlyList<(Guid OrgId, AuditChainFinding Finding)> broken)
    : Exception($"audit chain verification failed for {broken.Count} org(s): "
        + string.Join(", ", broken.Take(5).Select(b => $"{b.OrgId}:{b.Finding.Fault}@{b.Finding.Seq}")))
{
    /// <summary>The orgs whose chain failed, with the first fault of each.</summary>
    public IReadOnlyList<(Guid OrgId, AuditChainFinding Finding)> Broken { get; } = broken;
}

/// <summary>
/// Platform task: lists the orgs that have an audit trail through the sweep role's definer
/// function, then verifies each chain inside that org's own tenant scope (so RLS, not this
/// code, keeps the walk to one org). A fault is logged at error level, counted, and thrown so
/// the task is retried and finally dead-lettered where operators see it.
/// </summary>
/// <remarks>
/// The plan also names a <c>security_event</c> row (AC6, 11.5.1's surface): <c>0053</c> (11.5.1-T5.1) ships the table, but
/// the closed V1 type set has no verifier-fault type (11.1.1-T4.1; carried row "G8 verifier fault has no `security_event` type
/// (11.5.1-T5, S137)") and <c>org_id</c> is NOT NULL (D-S137-OWNER, DECISIONS §8.2: an org-less event goes to a separate, still-owed <c>platform_security_event</c> table, never a nullable <c>org_id</c>): log line, counter, dead letter.
/// </remarks>
public sealed partial class VerifyAuditChainTaskHandler(
    IWorkerSchedDataSource sweep,
    ITenantScopeFactory scopes,
    ITenantDb db,
    ILogger<VerifyAuditChainTaskHandler> logger) : ITaskHandler<VerifyAuditChainPayload>
{
    private static readonly Meter Meter = new("Evidgate.Audit");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("audit_chain_verification_failures_total");

    /// <inheritdoc />
    public string TaskType => AuditSweeps.VerifyChainTaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(VerifyAuditChainPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var broken = new List<(Guid, AuditChainFinding)>();
        Guid? after = null;

        while (true)
        {
            var orgs = await ListOrgsAsync(after, payload.OrgBatch, cancellationToken).ConfigureAwait(false);

            foreach (var org in orgs)
            {
                await using var orgScope = await scopes
                    .BeginAsync(org, projectId: null, readOnlyTransaction: true, cancellationToken)
                    .ConfigureAwait(false);

                var verifier = new AuditChainVerifier(db);
                if (await verifier.VerifyAsync(org, cancellationToken).ConfigureAwait(false) is { } finding)
                {
                    broken.Add((org, finding));
                    Failures.Add(1);
                    LogBroken(org, finding.Fault, finding.Seq);
                }
            }

            if (orgs.Count < payload.OrgBatch)
            {
                break;
            }

            after = orgs[^1];
        }

        if (broken.Count > 0)
        {
            throw new AuditChainBrokenException(broken);
        }
    }

    private async Task<List<Guid>> ListOrgsAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select evidgate.audit_org_ids(@after, @limit)", connection);
        command.Parameters.AddWithValue("after", (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);

        var orgs = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            orgs.Add(reader.GetGuid(0));
        }

        return orgs;
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Error, Message = "Audit chain of org {OrgId} is broken: {Fault} at seq {Seq}")]
    private partial void LogBroken(Guid orgId, AuditChainFault fault, long seq);
}

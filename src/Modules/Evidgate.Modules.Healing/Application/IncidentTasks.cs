using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Healing.Application;

/// <summary>The task types and the idempotency vocabulary of the incident recording path.</summary>
internal static class IncidentTasks
{
    /// <summary>
    /// The Healing module's own delivery of the frozen-evidence event (the dispatcher's subscription): kept apart from
    /// Policy's task so neither module's failure or dead letter holds the other.
    /// </summary>
    public const string EvidenceRevisionFrozen = "healing.evidence.revision_frozen";

    /// <summary>The task that records the incidents of one frozen revision.</summary>
    public const string RecordIncidents = "record_incidents";

    /// <summary>Operation name under which the freeze consumer remembers a revision it already enqueued.</summary>
    public const string EnqueueOperation = "healing.record_incidents.enqueue";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The natural key of one revision's recording: tenant, run and revision number.</summary>
    public static string NaturalKey(Guid orgId, Guid runId, int revisionNo) =>
        string.Create(CultureInfo.InvariantCulture, $"healing:incidents:{orgId:D}:{runId:D}:{revisionNo}");

    /// <summary>The idempotency key (within the tenant): run and revision number.</summary>
    public static string RequestKey(Guid runId, int revisionNo) =>
        string.Create(CultureInfo.InvariantCulture, $"{runId:D}:{revisionNo}");

    public static string ToJson(RecordIncidentsPayload payload) => JsonSerializer.Serialize(payload, WireOptions);
}

/// <summary>Payload of the <c>record_incidents</c> task: ids only, never attempt data or error text.</summary>
/// <param name="OrgId"><c>org_id</c>: the tenant, which must agree with the row's scope.</param>
/// <param name="RunId"><c>run_id</c>: the run whose frozen revision is recorded.</param>
/// <param name="EvidenceRevisionNo"><c>evidence_revision_no</c>: the frozen revision.</param>
internal sealed record RecordIncidentsPayload(Guid OrgId, Guid RunId, int EvidenceRevisionNo);

/// <summary>
/// The <c>record_incidents</c> consumer: opens incidents for one frozen revision inside the worker's tenant
/// transaction, under the system actor, from rows the database already holds. It places no network call, so the
/// scope-owning consumer commits and a failure retries and dead-letters under the queue's own rules.
/// </summary>
internal sealed partial class RecordIncidentsTaskHandler(
    IServiceScopeFactory serviceScopes,
    ILogger<RecordIncidentsTaskHandler> logger) : ITaskHandler<RecordIncidentsPayload>
{
    /// <inheritdoc />
    public string TaskType => IncidentTasks.RecordIncidents;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        RecordIncidentsPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null || payload.OrgId != scope.OrgId)
        {
            // The scope's org is the only identity the work may run under; a payload naming another is a misrouted row.
            throw new InvalidOperationException(
                "the record_incidents task has no tenant scope or names another org, so no incident is recorded");
        }

        if (payload.RunId == Guid.Empty || payload.EvidenceRevisionNo < 1)
        {
            throw new InvalidOperationException("the record_incidents task names no run or no revision number");
        }

        await using var services = serviceScopes.CreateAsyncScope();
        var record = services.ServiceProvider.GetRequiredService<RecordIncidentsForEvidenceRevisionHandler>();
        var result = await record
            .Handle(new RecordIncidentsForEvidenceRevision(payload.RunId, payload.EvidenceRevisionNo), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // Never swallow: a returned failure would let the consumer mark the row done with nothing recorded.
            throw new InvalidOperationException(
                $"recording incidents for run {payload.RunId:D} revision {payload.EvidenceRevisionNo} was refused "
                + $"({result.Error?.Code ?? "unknown"}), so the task is retried");
        }

        LogRecorded(logger, payload.RunId, payload.EvidenceRevisionNo, result.Value);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "run {RunId} revision {RevisionNo} recorded {Count} incident writes")]
    private static partial void LogRecorded(ILogger logger, Guid runId, int revisionNo, int count);
}

using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Healing.Application;

internal sealed record RecordIncidentsForEvidenceRevision(Guid RunId, int EvidenceRevision);

internal interface IIncidentRecorder
{
    Task<IncidentWrites> InsertAsync(Guid runId, FrozenIncidentFailures failures, CancellationToken cancellationToken);
}

internal sealed record IncidentWrites(List<Guid> Created, List<Guid> Attached);

// The worker owns the transaction and the audit flush: unlike an HTTP command this is not a user-authorized message,
// so the audit rows are recorded here under the system actor.
internal sealed class RecordIncidentsForEvidenceRevisionHandler(IFrozenIncidentFailures source, IIncidentRecorder recorder,
    IOutboxWriter outbox, IAuditTrail audit, ITenantDb db, ITenantScopeAccessor scopes)
{
    private static readonly AuditActor SystemActor = new(AuditActorTypes.System, "system");

    public async ValueTask<Result<int>> Handle(RecordIncidentsForEvidenceRevision command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Healing, nameof(RecordIncidentsForEvidenceRevision));
        var org = scopes.Require().OrgId;
        if (command.RunId == Guid.Empty || command.EvidenceRevision < 1)
        {
            return Result.Failure<int>(Error.Invalid("A run and a positive evidence revision are required."));
        }

        var failures = await source.ReadAsync(command.RunId, command.EvidenceRevision, cancellationToken).ConfigureAwait(false);
        if (failures is null)
        {
            return Result.Failure<int>(Error.NotFound("The run has no such frozen evidence revision."));
        }

        if (failures.Attempts.Count == 0)
        {
            return Result.Success<int>(0);
        }

        var writes = await recorder.InsertAsync(command.RunId, failures, cancellationToken).ConfigureAwait(false);
        var count = writes.Created.Count + writes.Attached.Count;
        if (count == 0)
        {
            return Result.Success<int>(0);
        }

        var ownsAudit = !audit.HasUnit;
        using var unit = audit.BeginUnit();
        foreach (var id in writes.Created)
        {
            await outbox.EnqueueAsync(IncidentCreated.Action, IncidentCreated.Payload(org, failures.ProjectId, command.RunId, id),
                "incident", id, $"healing:incident:{id:D}:created", cancellationToken).ConfigureAwait(false);
            audit.Describe(new AuditObject("incident", id.ToString("D")));
            audit.Record(IncidentCreated.Action, SystemActor);
        }

        foreach (var id in writes.Attached)
        {
            audit.Describe(new AuditObject("incident_attempt", id.ToString("D")));
            audit.Record("incident.attempt_attached", SystemActor);
        }

        if (ownsAudit)
        {
            await audit.FlushAsync(org, db, cancellationToken).ConfigureAwait(false);
        }

        // Counted after the commit this handler does not own, so a rolled-back attempt or a repeat adds nothing.
        var createdCount = writes.Created.Count;
        scopes.Require().AfterCommit(() => EvidgateTelemetry.RecordIncidentCreated(createdCount));

        return Result.Success<int>(count);
    }
}

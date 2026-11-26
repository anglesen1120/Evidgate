using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Healing.Domain;

/// <summary>The triage lifecycle of an incident; the database stores the lower-case name.</summary>
internal enum IncidentState
{
    Open,
    Triaged,
    Closed,
}

/// <summary>Whether the incident's context has been gathered; the database stores the lower-case name.</summary>
internal enum IncidentContextState
{
    Pending,
    Complete,
    Partial,
}

/// <summary>
/// A verified failure that healing may act on. The linkage to the failing run is fixed when the incident opens and
/// never changes; only the two state fields move, through the methods below. A refused transition returns a failed
/// <see cref="Result"/> and leaves every field as it was.
/// </summary>
internal sealed class Incident
{
    // EF materialises rows through this constructor.
    private Incident()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    /// <summary>The first failing attempt of the case in the run.</summary>
    public Guid AttemptId { get; private set; }

    public Guid CaseId { get; private set; }

    public Guid CaseRevisionId { get; private set; }

    public string ExecutionKey { get; private set; } = string.Empty;

    /// <summary>The adapter engine family of the run, for retrieval filtering.</summary>
    public string Engine { get; private set; } = string.Empty;

    public IncidentState State { get; private set; } = IncidentState.Open;

    public IncidentContextState ContextState { get; private set; } = IncidentContextState.Pending;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The optimistic-concurrency token; the database increments it on every update.</summary>
    public int Version { get; private set; } = 1;

    /// <summary>Opens an incident, <see cref="IncidentState.Open"/> with its context <see cref="IncidentContextState.Pending"/>.</summary>
    public static Incident Open(
        Guid orgId,
        Guid projectId,
        Guid runId,
        Guid attemptId,
        Guid caseId,
        Guid caseRevisionId,
        string executionKey,
        string engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);

        return new Incident
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            ProjectId = projectId,
            RunId = runId,
            AttemptId = attemptId,
            CaseId = caseId,
            CaseRevisionId = caseRevisionId,
            ExecutionKey = executionKey,
            Engine = engine,
        };
    }

    /// <summary>
    /// Moves <see cref="IncidentState.Open"/> to <see cref="IncidentState.Triaged"/>. The label is checked here and not
    /// stored: the row has no label column.
    /// </summary>
    public Result Triage(string? label)
    {
        if (State != IncidentState.Open)
        {
            return Refuse($"a {State} incident cannot be triaged");
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return Result.Failure(Error.Invalid("a triage label is required"));
        }

        State = IncidentState.Triaged;
        return Result.Success();
    }

    /// <summary>Moves <see cref="IncidentState.Triaged"/> to <see cref="IncidentState.Closed"/>; an open incident must be triaged first.</summary>
    public Result Close()
    {
        if (State != IncidentState.Triaged)
        {
            return Refuse($"a {State} incident cannot be closed");
        }

        State = IncidentState.Closed;
        return Result.Success();
    }

    /// <summary>Settles a pending context as <see cref="IncidentContextState.Complete"/> or <see cref="IncidentContextState.Partial"/>; both are final.</summary>
    public Result SettleContext(IncidentContextState settled)
    {
        if (ContextState != IncidentContextState.Pending)
        {
            return Refuse($"a {ContextState} context is already settled");
        }

        if (settled is not (IncidentContextState.Complete or IncidentContextState.Partial))
        {
            return Refuse("a pending context settles as complete or partial only");
        }

        ContextState = settled;
        return Result.Success();
    }

    private static Result Refuse(string message) => Result.Failure(Error.Conflict(ApiErrorCode.InvalidTransition, message));
}

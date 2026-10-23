namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// Run aggregate of the sprint-1 slice (01.1.2-T3.1): identity, the manifest it
/// was created from and the planned executions. Real transitions and job/lease
/// state (00 §8.1–§8.2) arrive with 04; nothing here writes to a database.
/// </summary>
internal sealed class Run
{
    private readonly List<string> _suiteKeys = [];
    private readonly List<string> _plannedExecutionIds = [];

    private Run(string runId, string projectRef, string manifestId)
    {
        RunId = runId;
        ProjectRef = projectRef;
        ManifestId = manifestId;
    }

    internal string RunId { get; }

    internal string ProjectRef { get; }

    internal string ManifestId { get; }

    /// <summary>Suite revision keys the catalog returned when the run was created.</summary>
    internal IReadOnlyList<string> SuiteKeys => _suiteKeys;

    /// <summary>Executions planned for this run (empty until <see cref="Plan"/>).</summary>
    internal IReadOnlyList<string> PlannedExecutionIds => _plannedExecutionIds;

    /// <summary>Creates a run for a project and a registered manifest (UUIDv7 id, 00 §7).</summary>
    internal static Run Create(string projectRef, string manifestId, IEnumerable<string> suiteKeys)
    {
        var run = new Run(Guid.CreateVersion7().ToString(), projectRef, manifestId);
        run._suiteKeys.AddRange(suiteKeys);

        return run;
    }

    /// <summary>Replaces the planned executions; planning twice keeps exactly one list.</summary>
    internal void Plan(IEnumerable<string> executionIds)
    {
        _plannedExecutionIds.Clear();
        _plannedExecutionIds.AddRange(executionIds);
    }
}

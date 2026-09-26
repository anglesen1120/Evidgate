namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// Catalog's exported service interface (00 §5.5). 01.1.2-T2.1 placeholder for
/// the sprint-1 skeleton: the orchestrator needs to know which suite revisions a
/// project may execute; 03 owns the real catalog (suites, revisions, fixtures).
/// </summary>
public interface ICatalog
{
    /// <summary>Published suite revisions of a project. Skeleton: fixed stubs for any project.</summary>
    ValueTask<IReadOnlyList<SuiteRevisionRef>> ListSuiteRevisionsAsync(
        string projectRef,
        CancellationToken cancellationToken);
}

/// <summary>A published suite revision an execution may be planned for.</summary>
public sealed record SuiteRevisionRef(string SuiteRef, string RevisionRef)
{
    /// <summary>Stable key of the pair, used by the orchestrator when planning.</summary>
    public string Key => $"{SuiteRef}@{RevisionRef}";
}

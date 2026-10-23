namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// Port for run storage, owned by the domain (00 §5.5 C4/C6: aggregates stay
/// plain C#, adapters live in Infrastructure). The sprint-1 slice ships the
/// in-memory adapter; 01.4.2/04 replace it behind this same interface.
/// </summary>
internal interface IRunRegistry
{
    /// <summary>Stores a run.</summary>
    void Add(Run run);

    /// <summary>Finds a run; null when it is unknown.</summary>
    Run? Find(string runId);
}

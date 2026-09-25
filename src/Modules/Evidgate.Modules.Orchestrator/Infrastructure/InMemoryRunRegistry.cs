using System.Collections.Concurrent;

namespace Evidgate.Modules.Orchestrator.Infrastructure;

/// <summary>
/// In-memory run storage of the sprint-1 slice (fakes only; no tenant scope, no
/// persistence).
/// </summary>
internal sealed class InMemoryRunRegistry : Domain.IRunRegistry
{
    private readonly ConcurrentDictionary<string, Domain.Run> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Add(Domain.Run run) => _runs[run.RunId] = run;

    /// <inheritdoc />
    public Domain.Run? Find(string runId) => _runs.GetValueOrDefault(runId);
}

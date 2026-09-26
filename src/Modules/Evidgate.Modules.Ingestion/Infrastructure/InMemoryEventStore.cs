using System.Collections.Concurrent;

namespace Evidgate.Modules.Ingestion.Infrastructure;

/// <summary>
/// In-memory event batch sink of the sprint-1 slice (fakes only; 04 owns the real
/// insert path with dedupe by <c>event_id</c> + payload hash, 00 §9.4).
/// </summary>
internal sealed class InMemoryEventStore
{
    private readonly ConcurrentDictionary<string, int> _acceptedByRun = new(StringComparer.Ordinal);

    /// <summary>Records an accepted batch for a run.</summary>
    internal void Accept(string runId, int eventCount) =>
        _acceptedByRun.AddOrUpdate(runId, eventCount, (_, current) => current + eventCount);
}

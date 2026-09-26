using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Catalog.Contracts;

namespace Evidgate.Modules.Catalog.Infrastructure;

/// <summary>
/// In-memory catalog of the sprint-1 slice (fakes only; 03 replaces it with the
/// tenant-scoped catalog). It starts its own <c>evidgate.module=catalog</c> span,
/// so a trace shows the orchestrator crossing into Catalog (AC6) even though the
/// call arrives through <see cref="ICatalog"/>.
/// </summary>
internal sealed class InMemorySuiteCatalog : ICatalog
{
    private static readonly IReadOnlyList<SuiteRevisionRef> StubRevisions =
    [
        new("smoke", "rev-0001"),
        new("regression", "rev-0001"),
    ];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SuiteRevisionRef>> ListSuiteRevisionsAsync(
        string projectRef,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Catalog,
            nameof(ListSuiteRevisionsAsync));

        return ValueTask.FromResult(StubRevisions);
    }
}

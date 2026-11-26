namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// Body of <c>GET /v1/projects/{p}/runs/decision-counts</c>: how many of the project's runs created at or after
/// <paramref name="Since"/> have each newest live verdict. The class name is the OpenAPI schema name.
/// </summary>
/// <param name="Since">The inclusive lower bound on <c>run.created_at</c> that was applied (the default when absent).</param>
/// <param name="Pass">Runs whose newest live verdict is PASS.</param>
/// <param name="Fail">Runs whose newest live verdict is FAIL.</param>
/// <param name="Inconclusive">Runs whose newest live verdict is INCONCLUSIVE.</param>
/// <param name="NoDecision">Scanned runs with no live evaluation yet (a shadow-only run is one of them).</param>
/// <param name="Scanned">Runs read; always <c>Pass + Fail + Inconclusive + NoDecision</c>.</param>
/// <param name="Truncated">True when more runs matched than the scan cap allows, so the counts cover the newest runs only.</param>
public sealed record RunDecisionCounts(
    DateTimeOffset Since,
    int Pass,
    int Fail,
    int Inconclusive,
    int NoDecision,
    int Scanned,
    bool Truncated);

using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 06.5.1-T1.1 / 00 §5.5 C7 over the <b>real</b> solution graph: 05's reconciler reaches 06's freeze as
/// <c>Evidgate.Modules.Evidence.Contracts</c> only. The freeze is an internal service call, not a Mediator
/// query/command and not a <c>/v1</c> route, so no module gains a project reference to another module's
/// implementation and the contract project stays free of module dependencies (06.5.1 §3, §8 C7/Authz row).
/// </summary>
public class EvidenceFreezeBoundaryTests
{
    [Fact]
    public void Freeze_OnlyContractsReferencedAcrossModules()
    {
        var graph = SolutionLayout.ProjectReferenceGraph();

        // The one edge the freeze adds: 05's Orchestrator consumes IEvidenceFreeze from the contract project.
        graph.Keys.ShouldContain("Evidgate.Modules.Orchestrator");
        graph.Keys.ShouldContain("Evidgate.Modules.Evidence.Contracts");
        graph["Evidgate.Modules.Orchestrator"].ShouldContain(
            "Evidgate.Modules.Evidence.Contracts",
            "the freeze is the 05 -> 06 cross-module service call: Orchestrator must reference the contract "
            + "project, never Evidgate.Modules.Evidence (00 §5.5 C7)");

        // No module project references another module's non-Contracts project anywhere in the solution.
        ArchitectureRules.ModuleToModuleViaContractsOnly(graph).ShouldBeEmpty();

        // The contract project itself depends on no module: the freeze DTOs are the leaf 05 can bind to.
        graph["Evidgate.Modules.Evidence.Contracts"].ShouldNotContain(
            reference => reference.StartsWith("Evidgate.Modules.", StringComparison.Ordinal),
            "Evidgate.Modules.Evidence.Contracts must not depend on any module project (00 §5.5 C7)");
    }
}

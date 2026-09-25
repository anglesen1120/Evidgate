using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.5 module-shape rules, exercised twice per rule: once against synthetic
/// inputs that must produce a violation, and once against the real solution
/// (vacuously clean until 01.1.2 adds the module projects).
/// </summary>
public class ModuleBoundariesTests
{
    /// <summary>
    /// AC1: a direct project reference from Evidgate.Modules.Orchestrator to
    /// Evidgate.Modules.Ingestion (bypassing .Contracts) must fail and name both
    /// projects. Seeded in-memory — the equivalent real-graph run is asserted by
    /// <see cref="Real_Solution_Has_No_Module_Reference_Violations"/>.
    /// </summary>
    [Fact]
    public void Module_To_Module_Must_Reference_Only_Contracts()
    {
        var seededGraph = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Evidgate.Modules.Orchestrator"] = ["Evidgate.Modules.Ingestion"],
            ["Evidgate.Modules.Ingestion"] = [],
        };

        var violations = ArchitectureRules.ModuleToModuleViaContractsOnly(seededGraph);

        var violation = violations.ShouldHaveSingleItem();
        violation.ShouldContain("Evidgate.Modules.Orchestrator");
        violation.ShouldContain("Evidgate.Modules.Ingestion");

        // The legal shape — module to module through .Contracts — produces none.
        var legalGraph = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Evidgate.Modules.Orchestrator"] = ["Evidgate.Modules.Ingestion.Contracts"],
            ["Evidgate.Modules.Ingestion.Contracts"] = [],
            ["Evidgate.Modules.Ingestion"] = ["Evidgate.Modules.Ingestion.Contracts"],
        };
        ArchitectureRules.ModuleToModuleViaContractsOnly(legalGraph).ShouldBeEmpty();
    }

    [Fact]
    public void Domain_Not_Infrastructure()
    {
        var fixtures = new[]
        {
            typeof(Evidgate.Modules.Sample.Domain.SampleAggregate),
            typeof(Evidgate.Modules.Sample.Domain.SampleValue),
            typeof(Evidgate.Modules.Sample.Domain.DomainTypeTouchingInfrastructure),
            typeof(Evidgate.Modules.Sample.Domain.DomainViaConstructorInjection),
        };

        var violations = ArchitectureRules.NoDomainReferencesInfrastructure(fixtures);

        // Both seeded violations are reported: the property reference and the
        // constructor-injected Infrastructure dependency.
        violations.Count.ShouldBe(2);
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.DomainTypeTouchingInfrastructure)));
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.DomainViaConstructorInjection)));
        violations.ShouldAllBe(v => v.Contains("Infrastructure"));

        // Without the seeded types the rule is clean.
        ArchitectureRules
            .NoDomainReferencesInfrastructure(
                fixtures.Where(t => !t.Name.Contains("Infrastructure") && !t.Name.Contains("ConstructorInjection")))
            .ShouldBeEmpty();
    }

    [Fact]
    public void No_Public_Types_Inside_Module()
    {
        const string moduleAssembly = "Evidgate.Modules.Sample";

        var violating = new[]
        {
            typeof(Evidgate.Modules.Sample.Domain.PublicTypeOutsideContractsOrEndpoints),
        };
        ArchitectureRules
            .NoPublicTypesOutsideContractsOrEndpoints(moduleAssembly, violating)
            .ShouldHaveSingleItem()
            .ShouldContain(nameof(Evidgate.Modules.Sample.Domain.PublicTypeOutsideContractsOrEndpoints));

        // Internal types anywhere, and public types in .Contracts/.Endpoints, are fine.
        var allowed = new[]
        {
            typeof(Evidgate.Modules.Sample.Domain.SampleAggregate),
            typeof(Evidgate.Modules.Sample.Application.InternalApplicationService),
            typeof(Evidgate.Modules.Sample.Contracts.SampleDto),
            typeof(Evidgate.Modules.Sample.Endpoints.SampleEndpoints),
        };
        ArchitectureRules
            .NoPublicTypesOutsideContractsOrEndpoints(moduleAssembly, allowed)
            .ShouldBeEmpty();
    }

    // ---- the same rules over the real solution ----------------------------

    [Fact]
    public void Real_Solution_Has_No_Module_Reference_Violations()
    {
        ArchitectureRules
            .ModuleToModuleViaContractsOnly(SolutionLayout.ProjectReferenceGraph())
            .ShouldBeEmpty();
    }

    [Fact]
    public void Real_Solution_Has_No_Domain_Infrastructure_Violations()
    {
        ArchitectureRules
            .NoDomainReferencesInfrastructure(SolutionLayout.EvidgateTypes())
            .ShouldBeEmpty();
    }

    [Fact]
    public void Real_Solution_Has_No_Public_Types_Inside_Modules()
    {
        var violations = SolutionLayout
            .EvidgateAssemblies()
            .SelectMany(assembly =>
            {
                var name = assembly.GetName().Name;
                var isModuleImplementation = name is not null
                    && name.StartsWith("Evidgate.Modules.", StringComparison.Ordinal)
                    && !name.EndsWith(".Contracts", StringComparison.Ordinal);

                return isModuleImplementation
                    ? ArchitectureRules.NoPublicTypesOutsideContractsOrEndpoints(
                        name!, ArchitectureRules.SafeGetTypes(assembly))
                    : Enumerable.Empty<string>();
            });
        violations.ShouldBeEmpty();
    }

    /// <summary>
    /// Wiring guard: the reflection rules only scan assemblies copied into this
    /// project's output, so every Evidgate.* project in the slnx must be a
    /// ProjectReference here — otherwise new modules would silently escape the
    /// checks. The reference is forced so trimming/linking cannot drop the copy.
    /// </summary>
    [Fact]
    public void Test_Project_References_All_Slnx_Projects()
    {
        var slnxProjects = SolutionLayout.SlnxProjectPaths()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != "Architecture.Tests" && n!.StartsWith("Evidgate.", StringComparison.Ordinal))
            .ToList();

        var referenced = SolutionLayout.TestProjectReferences();

        foreach (var project in slnxProjects)
        {
            referenced.ShouldContain(
                project,
                $"Architecture.Tests must ProjectReference {project} so the reflection rules scan it");
        }
    }

    /// <summary>The reflection scan must actually see the referenced assemblies.</summary>
    [Fact]
    public void Evidgate_Assemblies_Are_Loaded_From_Output()
    {
        SolutionLayout.EvidgateAssemblies().Select(a => a.GetName().Name)
            .ShouldContain("Evidgate.BuildingBlocks.SharedKernel");
    }
}

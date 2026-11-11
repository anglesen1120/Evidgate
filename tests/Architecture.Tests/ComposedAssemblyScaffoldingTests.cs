using System.Reflection;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// No scaffolding-named type ships in a composed assembly. The check is on the type name:
/// a class whose name starts with InMemory, Mock, Fake, Stub or Slice that is reachable from
/// <c>Evidgate.Api</c> or <c>Evidgate.Worker</c> ships in production, so it fails here rather
/// than after a deploy; a deliberate case is a recorded exemption. It is not a
/// dependency-injection registration check: the type need not be registered with any container.
/// </summary>
public sealed class ComposedAssemblyScaffoldingTests
{
    private static readonly string[] ForbiddenPrefixes = ["InMemory", "Mock", "Fake", "Stub", "Slice"];

    /// <summary>
    /// Prefix-named types shipped on purpose, each with the decision that allows it. Every
    /// entry must still exist, so a deletion cannot leave a stale permission behind.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Evidgate.Modules.Healing.Application.MockHealingLlmProvider"] =
            "the offline healing adapter, registered keyed and only inside the Healing module's IsDevelopment() branch: a composed production container has no registration to resolve, and the boot validator refuses the mock outside Development",
    };

    /// <summary>Names of the assemblies the two hosts reach through their project references.</summary>
    private static HashSet<string> CompositionAssemblies()
    {
        var graph = SolutionLayout.ProjectReferenceGraph();
        var reached = new HashSet<string>(StringComparer.Ordinal) { "Evidgate.Api", "Evidgate.Worker" };
        var pending = new Queue<string>(reached);

        while (pending.Count > 0)
        {
            if (!graph.TryGetValue(pending.Dequeue(), out var references))
            {
                continue;
            }

            foreach (var reference in references.Where(reached.Add))
            {
                pending.Enqueue(reference);
            }
        }

        return reached;
    }

    [Fact]
    public void No_InMemory_Mock_Fake_Stub_Or_Slice_Type_Ships_From_A_Composed_Assembly()
    {
        var composed = CompositionAssemblies();
        var types = SolutionLayout.EvidgateAssemblies()
            .Where(assembly => composed.Contains(assembly.GetName().Name ?? string.Empty))
            .SelectMany(Rules.ArchitectureRules.SafeGetTypes)
            .ToList();

        // Non-vacuity: an assembly set or a type list that resolves to nothing proves nothing.
        types.ShouldNotBeEmpty("the hosts' reference graph must resolve to real assemblies with real types");

        var scaffolding = types
            .Where(type => ForbiddenPrefixes.Any(prefix => type.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToDictionary(type => type.FullName ?? type.Name, StringComparer.Ordinal);

        var offenders = scaffolding.Keys.Except(Allowed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        offenders.ShouldBeEmpty("no InMemory*/Mock*/Fake*/Stub*/Slice* type may ship from a composed assembly: " + string.Join(", ", offenders));

        // A recorded allowance for a type that no longer exists would silently excuse a future one.
        var stale = Allowed.Keys.Except(scaffolding.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        stale.ShouldBeEmpty("these allowances name no composed scaffolding type: " + string.Join(", ", stale));
    }
}

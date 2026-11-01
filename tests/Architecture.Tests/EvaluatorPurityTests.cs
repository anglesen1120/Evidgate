using System.Reflection;
using System.Runtime.InteropServices;
using Architecture.Tests.Rules;
using Evidgate.Evaluation;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The evaluator is a pure function. Reaching a database driver through a contracts assembly, disabling the
/// banned-API diagnostic, or keeping static shared state must fail these assertions.
/// </summary>
public class EvaluatorPurityTests
{
    private const string EvaluatorDirectory = "src/Evidgate.Evaluation";

    [Fact]
    public void Real_Evaluator_Reaches_No_Io_And_No_Module_Implementation()
    {
        var closure = ArchitectureRules.ReachableAssemblyNames(typeof(PolicyEvaluator).Assembly);

        closure.Unresolved.ShouldBeEmpty();
        ArchitectureRules.EvaluatorStaysPure(closure).ShouldBeEmpty();
        closure.Reachable.ShouldContain("Evidgate.Modules.Evidence.Contracts");
    }

    [Fact]
    public void Real_Closure_Reaches_Assemblies_The_Direct_References_Hide()
    {
        var assembly = typeof(PolicyEvaluator).Assembly;
        var direct = ArchitectureRules.ReferencedAssemblyNames(assembly);
        var closure = ArchitectureRules.ReachableAssemblyNames(assembly);

        direct.ShouldNotContain("Evidgate.BuildingBlocks.SharedKernel");
        closure.Reachable.ShouldContain("Evidgate.BuildingBlocks.SharedKernel");
    }

    [Fact]
    public void Assembly_Walk_Stops_Only_At_The_Shared_Framework()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();

        ArchitectureRules.StopsAtSharedFramework($"{runtime}System.Runtime.dll", runtime).ShouldBeTrue();
        ArchitectureRules.StopsAtSharedFramework("/any/other/output/System.Runtime.dll", runtime).ShouldBeFalse();
        ArchitectureRules.StopsAtSharedFramework(
            "/build/src/BuildingBlocks/SharedKernel/Evidgate.BuildingBlocks.SharedKernel.dll",
            runtime).ShouldBeFalse();
    }

    [Fact]
    public void Unresolved_Reference_Fails_The_Purity_Guard()
    {
        ArchitectureRules.EvaluatorStaysPure(new ArchitectureRules.AssemblyClosure(
            ["Evidgate.Modules.Evidence.Contracts"],
            [])).ShouldBeEmpty();

        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Evidgate.Evaluation"] = ["Evidgate.Modules.Evidence.Contracts", "Evidgate.Unloadable.Contracts"],
            ["Evidgate.Modules.Evidence.Contracts"] = [],
        };

        var closure = ArchitectureRules.ReachableAssemblyNames(
            "Evidgate.Evaluation",
            name => graph[name],
            name => name != "Evidgate.Unloadable.Contracts");

        closure.Reachable.ShouldContain("Evidgate.Unloadable.Contracts");
        closure.Unresolved.ShouldBe(["Evidgate.Unloadable.Contracts"]);
        ArchitectureRules.EvaluatorStaysPure(closure)
            .ShouldHaveSingleItem()
            .ShouldContain("could not be resolved");
    }

    [Fact]
    public void Closure_Sees_A_Bridge_Behind_A_Contracts_Assembly()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Evidgate.Evaluation"] = ["Evidgate.Modules.Evidence.Contracts"],
            ["Evidgate.Modules.Evidence.Contracts"] = ["Transport.Bridge"],
            ["Transport.Bridge"] = ["Npgsql"],
            ["Npgsql"] = [],
        };

        var reachable = ArchitectureRules.ReachableAssemblyNames("Evidgate.Evaluation", name => graph[name]);

        ArchitectureRules.EvaluatorStaysPure(["Evidgate.Modules.Evidence.Contracts"]).ShouldBeEmpty();
        reachable.ShouldContain("Npgsql");
        reachable.ShouldContain("Transport.Bridge");
        ArchitectureRules.EvaluatorStaysPure(reachable)
            .ShouldHaveSingleItem()
            .ShouldContain("Npgsql");
    }

    [Fact]
    public void Purity_Rule_Rejects_An_Http_Db_Or_Module_Implementation_Reference()
    {
        var violations = ArchitectureRules.EvaluatorStaysPure([
            "System.Net.Http",
            "Npgsql",
            "Evidgate.Modules.Evidence",
            "Evidgate.Modules.Evidence.Contracts",
            "System.Runtime",
        ]);

        violations.Count.ShouldBe(3);
        violations.ShouldContain(violation => violation.Contains("System.Net.Http", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("Npgsql", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("Evidgate.Modules.Evidence", StringComparison.Ordinal));

        ArchitectureRules.EvaluatorStaysPure(["Evidgate.Modules.Evidence.Contracts", "System.Runtime"]).ShouldBeEmpty();
    }

    [Fact]
    public void Purity_Rule_Rejects_Any_Infrastructure_Assembly()
    {
        ArchitectureRules.EvaluatorStaysPure(["Evidgate.Modules.Evidence.Infrastructure"])
            .ShouldHaveSingleItem()
            .ShouldContain("infrastructure");
    }

    [Fact]
    public void Evaluator_Sources_Do_Not_Disable_The_Banned_Api_Diagnostic()
    {
        ArchitectureRules.BannedApiSuppressions(EvaluatorSources()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("#pragma warning disable RS0030\n")]
    [InlineData("#pragma warning disable\n")]
    [InlineData("#pragma warning disable RS0030// why\n")]
    [InlineData("#pragma warning disable RS0030 /* why */\n")]
    [InlineData("#pragma warning disable// why\n")]
    [InlineData("# pragma warning disable RS0030\n")]
    [InlineData("#  pragma warning disable\n")]
    [InlineData("#pragma warning disable CS0618, RS0030\n")]
    [InlineData("#pragma warning disable CS0618,RS0030\n")]
    [InlineData("#pragma  warning\tdisable   RS0030\n")]
    [InlineData("internal static class A;\n#pragma warning disable RS0030\ninternal static class B;\n")]
    [InlineData("[SuppressMessage(\"Design\", \"RS0030\", Justification = \"x\")]\n")]
    [InlineData("[SuppressMessage(\"Usage\", \"RS0030:Do not use banned APIs\")]\n")]
    [InlineData("[SuppressMessage(\n    category: \"Usage\",\n    checkId: \"RS0030:Do not use banned APIs\")]\n")]
    [InlineData("[SuppressMessageAttribute(\"Usage\", \"RS0030:Do not use banned APIs\")]\n")]
    public void Banned_Api_Rule_Flags_Every_Suppression_Shape(string source)
    {
        ArchitectureRules.Disables(source, "RS0030").ShouldBeTrue();

        ArchitectureRules.BannedApiSuppressions(new Dictionary<string, string>(StringComparer.Ordinal) { ["file.cs"] = source })
            .ShouldHaveSingleItem()
            .ShouldContain("file.cs");
    }

    [Theory]
    [InlineData("#pragma warning disable CS0618\n")]
    [InlineData("#pragma warning restore RS0030\ninternal static class Clean;\n")]
    [InlineData("[SuppressMessage(\"Design\", \"CA1062\")]\n")]
    [InlineData("[SuppressMessage(\"Design\", \"RS00300\")]\n")]
    [InlineData("#pragma warning disable CS0618 // RS0030 only in a comment\n")]
    [InlineData("// RS0030 is respected here\ninternal static class Clean;\n")]
    public void Banned_Api_Rule_Ignores_Text_That_Does_Not_Disable_The_Diagnostic(string source)
    {
        ArchitectureRules.Disables(source, "RS0030").ShouldBeFalse();

        ArchitectureRules.BannedApiSuppressions(new Dictionary<string, string>(StringComparer.Ordinal) { ["file.cs"] = source })
            .ShouldBeEmpty();
    }

    [Fact]
    public void Static_Mutable_Field_Survives_A_Hand_Applied_Compiler_Generated_Attribute()
    {
        HandMarkedCompilerGenerated.Counter = 1;

        ArchitectureRules.StaticMutableFields([typeof(HandMarkedCompilerGenerated)])
            .ShouldHaveSingleItem()
            .ShouldContain(nameof(HandMarkedCompilerGenerated.Counter));

        HandMarkedCompilerGenerated.Counter = 0;
    }

    [System.Runtime.CompilerServices.CompilerGenerated]
    private sealed class HandMarkedCompilerGenerated
    {
        public static int Counter;
    }

    [Fact]
    public void Banned_Api_Rule_Reports_Every_Offending_File_In_Path_Order()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pragmas.cs"] = "#pragma warning disable RS0030\n",
            ["attributes.cs"] = "[SuppressMessage(\"Design\", \"RS0030\", Justification = \"x\")]\n",
            ["clean.cs"] = "#pragma warning restore RS0030\ninternal static class Clean;\n",
        };

        var violations = ArchitectureRules.BannedApiSuppressions(sources);

        violations.Count.ShouldBe(2);
        violations[0].ShouldContain("attributes.cs");
        violations[1].ShouldContain("pragmas.cs");
    }

    [Fact]
    public void Banned_Symbols_Cover_The_Nondeterministic_Apis()
    {
        var banned = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, EvaluatorDirectory, "BannedSymbols.txt"));

        foreach (var symbol in new[]
        {
            "M:System.Guid.CreateVersion7",
            "T:System.Security.Cryptography.RandomNumberGenerator",
            "P:System.Environment.TickCount",
            "P:System.Environment.TickCount64",
            "M:System.Environment.GetEnvironmentVariable(",
            "T:System.Diagnostics.Stopwatch",
            "T:System.IO.File",
            "T:System.IO.Directory",
            "M:System.IO.Path.GetTempPath",
            "T:System.Diagnostics.Process",
            "M:System.Threading.Tasks.Task.Delay(",
            "M:System.Threading.Thread.Sleep(",
            "T:System.Console",
        })
        {
            banned.ShouldContain(symbol);
        }

        File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, EvaluatorDirectory, ".editorconfig"))
            .ShouldContain("dotnet_diagnostic.RS0030.severity = error");
    }

    [Fact]
    public void Evaluator_Assembly_Has_No_Static_Mutable_Fields()
    {
        var assembly = typeof(PolicyEvaluator).Assembly;

        ArchitectureRules.StaticMutableFields(ArchitectureRules.SafeGetTypes(assembly)).ShouldBeEmpty();
    }

    [Fact]
    public void Static_Mutable_Field_Rule_Flags_A_Shared_Mutable_Static()
    {
        SharedState.Counter = 1;
        SharedState.Items.Add(1);

        var violations = ArchitectureRules.StaticMutableFields([typeof(SharedState)]);

        violations.Count.ShouldBe(2);
        violations.ShouldContain(violation => violation.Contains("Counter", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("Items", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluator_Inputs_Expose_No_Mutable_Setters()
    {
        var mutable = typeof(PolicyEvaluator).Assembly.GetExportedTypes()
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.SetMethod is { } setter
                    && !setter.ReturnParameter.GetRequiredCustomModifiers()
                        .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        mutable.ShouldBeEmpty();
    }

    private static SortedDictionary<string, string> EvaluatorSources()
    {
        var root = Path.Combine(SolutionLayout.RepoRoot, EvaluatorDirectory);
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SolutionLayout.RepoRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            sources[relative] = File.ReadAllText(file);
        }

        return sources;
    }

    /// <summary>A type with the two shapes the static-state rule must reject.</summary>
    private static class SharedState
    {
        internal static int Counter;

        internal static readonly List<int> Items = [];
    }
}

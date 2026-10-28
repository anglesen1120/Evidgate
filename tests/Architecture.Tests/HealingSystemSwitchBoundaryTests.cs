using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 08.6.3-T1 (coordinator decision, ruling T72): the platform-wide healing switch is reachable through exactly one
/// writer. <c>evidgate.healing_switch_set_system</c> must be named only by the Healing module's operator writer —
/// never by a route, a handler, another module or a test — because every other path to the tenant-less row would be
/// an unaudited way to switch healing off (or on) for every org at once.
/// </summary>
/// <remarks>
/// A source scan, not a reference walk: the name may also appear as a string in the SQL constant of a module that
/// should not have it, which is precisely the case a compile-time check would miss. <c>migrations/</c> is excluded —
/// the file that defines the function necessarily names it.
/// </remarks>
public sealed class HealingSystemSwitchBoundaryTests
{
    private const string Needle = "healing_switch_set_system";

    private const string AllowedFile = "src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs";

    /// <summary>The only source file of <c>src/</c> that names the definer is the Healing operator writer.</summary>
    [Fact]
    public void Only_the_healing_operator_writer_names_the_system_switch_definer()
    {
        var hits = Scan(Path.Combine(SolutionLayout.RepoRoot, "src"));

        hits.ShouldBe([AllowedFile]);
    }

    /// <summary>No test may call the definer directly: the operator route and the denied gate are what is tested.</summary>
    [Fact]
    public void No_test_names_the_system_switch_definer() =>
        Scan(Path.Combine(SolutionLayout.RepoRoot, "tests")).ShouldBeEmpty();

    /// <summary>
    /// Non-vacuity: the allowed file really carries the name in its SQL constant, so the scan above is not empty
    /// because the literal changed shape everywhere.
    /// </summary>
    [Fact]
    public void The_allowed_file_carries_the_definer_call()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, AllowedFile));

        text.ShouldContain("evidgate." + Needle);
    }

    private static List<string> Scan(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(Needle, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(SolutionLayout.RepoRoot, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}

using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 08.6.3-T1 (coordinator decision, ruling T72): the platform-wide healing switch is reachable through exactly one
/// writer. <c>evidgate.healing_switch_set_system</c> must be <em>called</em> only by the Healing module's operator
/// writer — never by a route, a handler, another module or a new test — because every other production path to the
/// tenant-less row would be an unaudited way to switch healing off (or on) for every org at once.
/// </summary>
/// <remarks>
/// A source scan, not a reference walk: the name may also appear as a string in the SQL constant of a module that
/// should not have it, which is precisely the case a compile-time check would miss. The needle is the function's own
/// name, so naming it at all — a call, a resolver table entry or a comment in the wrong assembly — is what trips the
/// scan; a real call is then pinned separately by <see cref="The_allowed_file_carries_the_definer_call"/>.
/// <c>migrations/</c> is excluded: the file that defines the function necessarily names it.
/// <para>
/// Five test files legitimately name it: <c>HealingSwitchSchemaTests</c> and its
/// <c>PlatformOperator</c> part exercise the definer against a database (grants, the tenant-context refusal, the
/// upsert, the audit row), <c>Isolation.Tests/Rls/ResolverTests</c> maps the function to the migration that defines it
/// for the RLS resolver, <c>Migrator.IntegrationTests/PlatformOperatorUpgradeMigrationTests</c> asserts the 0105
/// upgrade keeps the definer's grants, and this boundary test declares the needle and the allowlist. Any
/// <em>other</em> test naming it fails the scan, because a test that can call the definer can also write the platform
/// row behind the operator gate.
/// </para>
/// </remarks>
public sealed class HealingSystemSwitchBoundaryTests
{
    /// <summary>The definer's name, without its argument list, so a mention counts as a reference too.</summary>
    private const string Needle = "healing_switch_set_system";

    private const string AllowedFile = "src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs";

    /// <summary>
    /// The only test files that may spell the call: the definer's own integration suite, the RLS resolver's
    /// function-to-migration table, the migrator's 0105 upgrade test (asserts the upgrade keeps the definer's
    /// grants), and this boundary test’s own needle (declared above).
    /// </summary>
    private static readonly string[] AllowedTestFiles =
    [
        "tests/Architecture.Tests/HealingSystemSwitchBoundaryTests.cs",
        "tests/Isolation.Tests/Rls/ResolverTests.cs",
        "tests/Migrator.IntegrationTests/PlatformOperatorUpgradeMigrationTests.cs",
        "tests/Tenancy.IntegrationTests/HealingSwitchSchemaTests.PlatformOperator.cs",
        "tests/Tenancy.IntegrationTests/HealingSwitchSchemaTests.cs",
    ];

    /// <summary>The only source file of <c>src/</c> that names the definer is the Healing operator writer.</summary>
    [Fact]
    public void Only_the_healing_operator_writer_names_the_system_switch_definer() =>
        Scan(Path.Combine(SolutionLayout.RepoRoot, "src")).ShouldBe([AllowedFile]);

    /// <summary>
    /// No other test may call the definer: the named allowlist, and nothing else, so a new suite that reaches for the
    /// platform row behind the operator gate is refused here instead of in review.
    /// </summary>
    [Fact]
    public void Only_the_allowlisted_tests_name_the_system_switch_definer() =>
        Scan(Path.Combine(SolutionLayout.RepoRoot, "tests")).ShouldBe(AllowedTestFiles.Order(StringComparer.Ordinal));

    /// <summary>
    /// Non-vacuity: the allowed file really carries the SQL <em>call</em> — not merely the name in prose — so the scans
    /// above are not empty because the literal changed shape everywhere.
    /// </summary>
    [Fact]
    public void The_allowed_file_carries_the_definer_call()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, AllowedFile));

        text.ShouldContain("from evidgate." + Needle + "(");
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

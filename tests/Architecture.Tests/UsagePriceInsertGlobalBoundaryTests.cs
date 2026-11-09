using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The global price card is written through exactly one caller. <c>evidgate.usage_price_insert_global</c> is granted
/// to the app role and refuses a tenant transaction, but nothing at the database tells one app-role caller from
/// another, so a second production path to it would be an unreviewed way to add a platform price. The source scan
/// names the single allowed store and the test files that exercise the definer or map it to its migration.
/// </summary>
/// <remarks>
/// A source scan like the healing switch boundary: the function's own name is the needle, so a call, a resolver table
/// entry or a comment in the wrong assembly trips it. <c>migrations/</c> is excluded because the defining file names it.
/// </remarks>
public sealed class UsagePriceInsertGlobalBoundaryTests
{
    private const string Needle = "usage_price_insert_global";

    private const string AllowedFile = "src/Modules/Evidgate.Modules.Usage/Infrastructure/Persistence/UsagePriceStore.cs";

    private static readonly string[] AllowedTestFiles =
    [
        "tests/Architecture.Tests/UsagePriceInsertGlobalBoundaryTests.cs",
        "tests/Isolation.Tests/Rls/ResolverTests.cs",
        "tests/Tenancy.IntegrationTests/DefinerExecuteScopeTests.cs",
        "tests/Tenancy.IntegrationTests/UsagePriceSchemaTests.Helpers.cs",
        "tests/Tenancy.IntegrationTests/UsagePriceSchemaTests.Privileges.cs",
    ];

    [Fact]
    public void Only_the_usage_price_store_names_the_global_insert_definer() =>
        Scan(Path.Combine(SolutionLayout.RepoRoot, "src")).ShouldBe([AllowedFile]);

    [Fact]
    public void Only_the_allowlisted_tests_name_the_global_insert_definer() =>
        Scan(Path.Combine(SolutionLayout.RepoRoot, "tests")).ShouldBe(AllowedTestFiles.Order(StringComparer.Ordinal));

    /// <summary>
    /// The loader is the only application path to the store's global write, so a new production caller of it is as
    /// unreviewed a route to a platform price as a second SQL call. Today it is only declared and registered.
    /// </summary>
    [Fact]
    public void Only_the_usage_module_declares_and_registers_the_price_loader() =>
        Scan(Path.Combine(SolutionLayout.RepoRoot, "src"), "UsagePriceLoader").ShouldBe(
        [
            "src/Modules/Evidgate.Modules.Usage/Application/UsagePriceLoader.cs",
            "src/Modules/Evidgate.Modules.Usage/UsageModule.cs",
        ]);

    /// <summary>Non-vacuity: the allowed store really carries the SQL call, not merely the name in prose.</summary>
    [Fact]
    public void The_allowed_file_carries_the_definer_call()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, AllowedFile));

        text.ShouldContain("evidgate." + Needle + "(");
    }

    private static List<string> Scan(string root, string needle = Needle)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(needle, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(SolutionLayout.RepoRoot, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}

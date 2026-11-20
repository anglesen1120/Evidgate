using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Pins the shared <c>src/**/*.cs</c> walk every source-text architecture rule reads, so a filter change that
/// silently hides files turns a rule green for the wrong reason.
/// </summary>
public class ProductionSourceWalkTests
{
    private readonly ITestOutputHelper output;

    public ProductionSourceWalkTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void RealTree_Walk_Is_Sorted_Relative_And_Free_Of_Build_Output()
    {
        var files = SolutionLayout.ProductionSourceFiles();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', files)))).ToLowerInvariant();
        output.WriteLine($"production source files: {files.Count}, sorted-path sha256[:16]: {hash[..16]}");

        files.ShouldBe(files.Order(StringComparer.Ordinal).ToList());
        files.ShouldNotContain(path => path.Contains("/obj/", StringComparison.Ordinal) || path.Contains("/bin/", StringComparison.Ordinal));
        files.ShouldContain("src/Modules/Evidgate.Modules.Identity/Endpoints/LoginEndpoints.cs");
        files.ShouldContain("src/Modules/Evidgate.Modules.Identity/Ci/ExchangeRateLimiter.cs");
        files.ShouldContain("src/Evidgate.Migrator/DevSeedToken.cs");
        files.ShouldAllBe(path => path.StartsWith("src/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal));
    }

    [Fact]
    public void IsBuildOutput_Looks_Only_Below_The_Source_Root_And_Only_At_Directories()
    {
        // A checkout under a directory named bin or obj must not hide every file.
        var srcRoot = Path.Combine(Path.GetTempPath(), "bin", "obj", "checkout", "src");

        SolutionLayout.IsBuildOutput(srcRoot, Path.Combine(srcRoot, "Api", "Host.cs")).ShouldBeFalse();
        SolutionLayout.IsBuildOutput(srcRoot, Path.Combine(srcRoot, "Api", "obj", "Debug", "Gen.cs")).ShouldBeTrue();
        SolutionLayout.IsBuildOutput(srcRoot, Path.Combine(srcRoot, "bin", "Gen.cs")).ShouldBeTrue();

        // A file or directory that merely contains the word is not build output.
        SolutionLayout.IsBuildOutput(srcRoot, Path.Combine(srcRoot, "Api", "obj.cs")).ShouldBeFalse();
        SolutionLayout.IsBuildOutput(srcRoot, Path.Combine(srcRoot, "binary", "Host.cs")).ShouldBeFalse();
    }

    /// <summary>
    /// The subtrees the source-text rules read through <see cref="SolutionLayout.SourceFiles(string)"/>: the count
    /// measured when the rule moved to the shared walk (a floor, so adding a file needs no edit) and a file that
    /// must be in it. A failure names the row: a file moved or deleted, so update the row, or the walk lost a subtree.
    /// </summary>
    public static TheoryData<string, int, string> Subtrees => new()
    {
        { "src/Evidgate.Evaluation", 14, "src/Evidgate.Evaluation/PolicyEvaluator.cs" },
        { "src/Modules/Evidgate.Modules.Orchestrator", 70, "src/Modules/Evidgate.Modules.Orchestrator/Endpoints/ReleaseEndpoints.cs" },
        { "src/Modules/Evidgate.Modules.Catalog", 54, "src/Modules/Evidgate.Modules.Catalog/Endpoints/CatalogEndpoints.cs" },
        { "src/Modules/Evidgate.Modules.Policy/Infrastructure/Persistence", 6, "src/Modules/Evidgate.Modules.Policy/Infrastructure/Persistence/EvaluationSql.cs" },
        { "src", 1050, "src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs" },
        { "tests", 1172, "tests/Architecture.Tests/SolutionLayout.cs" },
    };

    [Theory]
    [MemberData(nameof(Subtrees))]
    public void RealTree_Subtree_Keeps_Its_Floor_And_Anchor(string root, int floor, string anchor)
    {
        var files = SolutionLayout.SourceFiles(root);
        output.WriteLine($"{root}: {files.Count} files");

        files.Count.ShouldBeGreaterThanOrEqualTo(floor, $"{root} lost files; if intended, lower its row in {nameof(Subtrees)}");
        files.ShouldContain(anchor);
        files.ShouldAllBe(path => path.StartsWith(root + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceFiles_On_A_Temp_Tree_Drops_Build_Output_And_Sees_A_Deleted_File_Go()
    {
        var repo = Directory.CreateTempSubdirectory("walk");
        try
        {
            foreach (var relative in new[] { "src/A/Kept.cs", "src/A/Other.cs", "src/A/obj/Debug/Gen.cs", "src/bin/Gen.cs", "src/A/notes.txt", "src/A/binary/Keep.cs", "src/A/obj.cs", "src/A/Permissions.g.cs" })
            {
                var full = Path.Combine(repo.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "x");
            }

            SolutionLayout.SourceFiles(repo.FullName, "src").ShouldBe(["src/A/Kept.cs", "src/A/Other.cs", "src/A/Permissions.g.cs", "src/A/binary/Keep.cs", "src/A/obj.cs"]);
            SolutionLayout.SourceFiles(repo.FullName, "src/A").ShouldBe(["src/A/Kept.cs", "src/A/Other.cs", "src/A/Permissions.g.cs", "src/A/binary/Keep.cs", "src/A/obj.cs"]);

            File.Delete(Path.Combine(repo.FullName, "src/A/Other.cs"));
            SolutionLayout.SourceFiles(repo.FullName, "src").ShouldBe(["src/A/Kept.cs", "src/A/Permissions.g.cs", "src/A/binary/Keep.cs", "src/A/obj.cs"]);

            Should.Throw<DirectoryNotFoundException>(() => SolutionLayout.SourceFiles(repo.FullName, "src/Missing"));
        }
        finally
        {
            repo.Delete(recursive: true);
        }
    }
}

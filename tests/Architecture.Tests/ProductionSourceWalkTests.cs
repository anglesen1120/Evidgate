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
    /// <summary>Well below the real tree; the point is to catch a walk that lost a whole subtree.</summary>
    private const int MinimumFiles = 1000;

    private readonly ITestOutputHelper output;

    public ProductionSourceWalkTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void RealTree_Walk_Is_Sorted_Relative_And_Free_Of_Build_Output()
    {
        var files = SolutionLayout.ProductionSourceFiles();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', files)))).ToLowerInvariant();
        output.WriteLine($"production source files: {files.Count}, sorted-path sha256[:16]: {hash[..16]}");

        files.Count.ShouldBeGreaterThanOrEqualTo(MinimumFiles);
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
}

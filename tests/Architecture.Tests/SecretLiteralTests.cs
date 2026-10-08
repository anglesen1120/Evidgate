using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 10.1.1-T2 (AC3): every secret resolution goes through <c>ISecretProvider</c>, so no
/// source file outside the secret seam may spell a secret key as a literal or read the
/// process environment. The rule is exercised twice: once against synthetic inputs that
/// must produce a violation, and once against the real solution (which must be clean).
/// </summary>
public class SecretLiteralTests
{
    /// <summary>
    /// A clean checkout has several hundred hand-written <c>src/**/*.cs</c> files; a scan
    /// that sees fewer than this filtered the tree away (e.g. a checkout under a
    /// <c>bin</c> directory) and would pass vacuously.
    /// </summary>
    private const int MinimumScannedFiles = 200;

    /// <summary>The only file allowed to spell a secret key: the name registry itself.</summary>
    private static readonly string[] ExemptPaths =
    [
        "src/BuildingBlocks/SharedKernel/Secrets/SecretNames.cs",
    ];

    [Fact]
    public void Synthetic_Bypass_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Api/Bypass.cs", "var pepper = configuration[\"EVIDGATE:Ci:Pepper\"];"),
            ("src/Evidgate.Api/Clean.cs", "var pepper = secrets.GetSecretAsync(SecretNames.CiPepper);"),
        };

        var violations = SecretLiteralRules.FindViolations(files, ExemptPaths);

        violations.ShouldHaveSingleItem().ShouldContain("src/Evidgate.Api/Bypass.cs");
    }

    [Theory]
    [InlineData("var pepper = configuration[\"EVIDGATE:CI:PEPPER\"];")]
    [InlineData("var pepper = configuration[\"evidgate:ci:pepper\"];")]
    [InlineData("var value = Environment.GetEnvironmentVariable(\"evidgate__ci__pepper\");")]
    public void Synthetic_Key_In_Any_Case_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[] { ("src/Evidgate.Api/Bypass.cs", text) };

        SecretLiteralRules.FindViolations(files, ExemptPaths)
            .ShouldContain(violation => violation.Contains("secret key literal", StringComparison.Ordinal));
    }

    [Fact]
    public void Synthetic_Environment_Name_Bypass_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Worker/Bypass.cs", "var value = Environment.GetEnvironmentVariable(\"EVIDGATE__CI__PEPPER\");"),
        };

        var violations = SecretLiteralRules.FindViolations(files, ExemptPaths);

        violations.ShouldContain(violation => violation.Contains("secret key literal", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("reads the process environment", StringComparison.Ordinal));
    }

    [Fact]
    public void Synthetic_Environment_Constant_Read_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Worker/Bypass.cs", "var value = Environment.GetEnvironmentVariable(SecretNames.CiPepper);"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldHaveSingleItem();
    }

    [Fact]
    public void Synthetic_Environment_Constant_Read_Is_Detected_Even_In_An_Environment_Exempt_File()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Migrator/DevSeed.cs", "var value = Environment.GetEnvironmentVariable(SecretNames.MigratorConnectionString);"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("var value = Environment.GetEnvironmentVariable(\"EVIDGATE_SOMETHING\");")]
    [InlineData("foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) { }")]
    public void Synthetic_Any_Environment_Read_Outside_The_Exemptions_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[] { ("src/Evidgate.Worker/Bypass.cs", text) };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldHaveSingleItem()
            .ShouldContain("reads the process environment");
    }

    [Fact]
    public void Environment_Read_Exemptions_Name_Existing_Files_With_A_Reason()
    {
        foreach (var (path, reason) in SecretLiteralRules.EnvironmentReadExemptions)
        {
            File.Exists(Path.Combine(SolutionLayout.RepoRoot, path)).ShouldBeTrue(path);
            reason.ShouldNotBeNullOrWhiteSpace(path);
        }

        File.Exists(Path.Combine(SolutionLayout.RepoRoot, SecretLiteralRules.MigratorAliasReadExemptPath))
            .ShouldBeTrue(SecretLiteralRules.MigratorAliasReadExemptPath);
    }

    [Theory]
    [InlineData("var value = environment[MigratorOptions.ConnectionStringVariable];")]
    [InlineData("values.TryGetValue(MigratorOptions.ConnectionStringVariable, out var value);")]
    [InlineData("var value = configuration.GetValue<string>(MigratorOptions.ConnectionStringVariable);")]
    public void Synthetic_Migrator_Alias_Read_Outside_MigratorOptions_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[] { ("src/Evidgate.Migrator/MigratorHost.cs", text) };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldHaveSingleItem()
            .ShouldContain("ConnectionStringVariable");
    }

    [Fact]
    public void Migrator_Alias_As_A_Name_Is_Allowed()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Migrator/MigratorHost.cs", "var help = $\"{MigratorOptions.ConnectionStringVariable} (required)\";"),
            ("src/Evidgate.Migrator/MigratorOptions.cs", "values.TryGetValue(ConnectionStringVariable, out var value);"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldBeEmpty();
    }

    [Fact]
    public void Synthetic_GetConnectionString_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Evidgate.Api/Bypass.cs", "var value = configuration.GetConnectionString(\"EVIDGATE:Database\");"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("var value = configuration.GetSection(\"EVIDGATE:Database\")[\"ConnectionString\"];")]
    [InlineData("var value = configuration.GetSection(\"EVIDGATE:Database\")[\"connectionstring\"];")]
    [InlineData("var value = section.GetValue<string>(\"PEPPER\");")]
    public void Synthetic_Property_Read_Is_Detected(string text)
    {
        var files = new (string Path, string Text)[] { ("src/Evidgate.Api/Bypass.cs", text) };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldHaveSingleItem();
    }

    [Fact]
    public void Property_Name_As_A_Collection_Element_Is_Not_A_Read()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/BuildingBlocks/Observability/Redaction/Markers.cs", "string[] markers =\n[\n    \"connectionstring\",\n    \"dsn\",\n];"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldBeEmpty();
    }

    [Fact]
    public void Exempt_File_Is_Skipped()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/BuildingBlocks/SharedKernel/Secrets/SecretNames.cs", "public const string CiPepper = \"EVIDGATE__CI__PEPPER\";"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldBeEmpty();
    }

    [Fact]
    public void Seam_Files_Other_Than_SecretNames_Are_Scanned()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/BuildingBlocks/SharedKernel/Secrets/EnvironmentSecretProvider.cs", "var value = configuration[\"EVIDGATE:Ci:Pepper\"];"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldNotBeEmpty();
    }

    [Fact]
    public void Longer_Key_Is_Not_A_False_Positive()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Identity/Ci/CiSecrets.cs", "failures.Add(\"EVIDGATE:Ci:PreviousPepperVersion must differ from PepperVersion\");"),
        };

        SecretLiteralRules.FindViolations(files, ExemptPaths).ShouldBeEmpty();
    }

    [Fact]
    public void Generated_Filter_Uses_The_Path_Relative_To_The_Source_Root()
    {
        // A checkout under a directory named bin (or obj) must not hide every file.
        var srcRoot = Path.Combine(Path.GetTempPath(), "bin", "obj", "checkout", "src");

        IsGenerated(srcRoot, Path.Combine(srcRoot, "Evidgate.Api", "ApiHost.cs")).ShouldBeFalse();
        IsGenerated(srcRoot, Path.Combine(srcRoot, "Evidgate.Api", "obj", "Debug", "Generated.cs")).ShouldBeTrue();
        IsGenerated(srcRoot, Path.Combine(srcRoot, "Evidgate.Api", "bin", "Release", "Copied.cs")).ShouldBeTrue();
    }

    [Fact]
    public void Real_Solution_Has_No_Secret_Key_Literals()
    {
        var files = SourceFiles();

        files.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumScannedFiles,
            $"only {files.Count} src/**/*.cs files were scanned; the generated-file filter is hiding the tree");

        var violations = SecretLiteralRules.FindViolations(files, ExemptPaths);

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    /// <summary>Every hand-written <c>src/**/*.cs</c> file, repo-relative with '/'.</summary>
    private static List<(string Path, string Text)> SourceFiles()
    {
        var root = Path.Combine(SolutionLayout.RepoRoot, "src");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGenerated(root, path))
            .Select(path => (Relative(path), File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>
    /// True when a directory below <paramref name="srcRoot"/> is a build output
    /// (<c>bin</c> or <c>obj</c>). Only the part of the path below the source root is
    /// inspected, so the directories the checkout itself lives in never count.
    /// </summary>
    private static bool IsGenerated(string srcRoot, string path)
    {
        var relative = Path.GetRelativePath(srcRoot, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments[..^1].Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal)
            || string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(SolutionLayout.RepoRoot, path).Replace('\\', '/');
}

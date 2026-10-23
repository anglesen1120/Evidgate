namespace Contract.Tests;

/// <summary>
/// Repository paths this suite needs. Every shared artifact is read from the
/// repository — <c>contracts/</c> for the schemas under test and
/// <c>fixtures/contracts/</c> for the parity corpus — because the CLI's Vitest
/// suite asserts the same files (01.3.2 AC3); they are never duplicated into
/// this project's build output. Named <c>ContractPaths</c> (not
/// <c>RepoPaths</c>) so the later 01.3.2-T3 test files can add their own helper
/// without a name collision.
/// </summary>
internal static class ContractPaths
{
    public static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>The repository's <c>contracts/</c> directory.</summary>
    public static string ContractsDirectory => Path.Combine(RepoRoot, "contracts");

    /// <summary>The repository's <c>fixtures/contracts/</c> corpus directory.</summary>
    public static string FixturesDirectory => Path.Combine(RepoRoot, "fixtures", "contracts");

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}

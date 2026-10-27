namespace Parser.Tests;

/// <summary>
/// Repository paths the child-protocol tests need. Both inputs are read from the repository instead of being
/// copied into the build output: <c>fixtures/reports/junit/sample/input.xml</c> is the shared JUnit corpus file
/// and <c>fixtures/hostile/</c> is the 06.3.2 hostile corpus (with its <c>manifest.json</c> reasons), so the child
/// is asserted against the exact bytes the in-process profile is asserted against.
/// </summary>
internal static class RepoPaths
{
    public static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>A file under the repository's <c>fixtures/</c> directory.</summary>
    public static string Fixtures(string relativePath) =>
        Path.Combine(RepoRoot, "fixtures", relativePath);

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

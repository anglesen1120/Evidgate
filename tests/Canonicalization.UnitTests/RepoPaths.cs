namespace Canonicalization.UnitTests;

/// <summary>
/// Repository paths a test needs. Every shared vector file is read from the
/// repository's <c>fixtures/</c> directory: the RFC 8785 reference pairs
/// (<c>fixtures/rfc8785/</c>) and the golden documents
/// (<c>fixtures/canonical/</c>) are asserted by the CLI's Vitest specs as well, so
/// they are never duplicated into this project's build output.
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

namespace Evidgate.Testing;

/// <summary>
/// The repository root every file-based test resolves against: the nearest ancestor of the test binary that holds
/// <c>Evidgate.slnx</c>. Shared source, linked into each test project that reads a committed file.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>The repository root.</summary>
    public static readonly string RepoRoot = Locate();

    private static string Locate()
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

        throw new InvalidOperationException(
            $"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}

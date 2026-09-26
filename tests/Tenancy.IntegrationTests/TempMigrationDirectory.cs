using System.Globalization;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A scratch migration directory holding a subset of the repository's files, so a
/// test can bring a database to a previous head first and then prove that the
/// remaining files apply forward — the deploy path (01.4.1 §11), which applying
/// the whole directory at once cannot show.
/// </summary>
internal sealed class TempMigrationDirectory : IDisposable
{
    private TempMigrationDirectory(string path) => Path = path;

    /// <summary>The scratch directory handed to the Migrator.</summary>
    internal string Path { get; }

    /// <summary>Copies every <c>migrations/</c> file whose name starts with one of <paramref name="numberPrefixes"/>.</summary>
    internal static TempMigrationDirectory CreateFrom(string sourceDirectory, params string[] numberPrefixes)
    {
        var scratch = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "evidgate-tenancy-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(scratch);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.sql"))
        {
            var name = System.IO.Path.GetFileName(file);

            if (numberPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                File.Copy(file, System.IO.Path.Combine(scratch, name), overwrite: true);
            }
        }

        return new TempMigrationDirectory(scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory must never fail the run.
        }
    }
}

using System.Globalization;

namespace Migrator.IntegrationTests;

/// <summary>
/// A throwaway migrations directory. Tests build the exact set under test —
/// including deliberately broken sets for the validation and failure cases — so
/// nothing test-only ever lands in the repository's <c>migrations/</c>.
/// </summary>
internal sealed class TempMigrations : IDisposable
{
    private TempMigrations(DirectoryInfo root) => Root = root;

    internal DirectoryInfo Root { get; }

    internal string Path => Root.FullName;

    /// <summary>Creates an empty migrations directory under the system temp directory.</summary>
    internal static TempMigrations Create() =>
        new(Directory.CreateTempSubdirectory("evidgate-migrations-"));

    /// <summary>Adds or replaces a migration file.</summary>
    internal TempMigrations Add(string fileName, string sql)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, fileName), sql);
        return this;
    }

    /// <summary>Copies one of the repository's real migrations into this directory.</summary>
    internal TempMigrations CopyReal(params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            File.Copy(
                System.IO.Path.Combine(PostgresMigratorFixture.RepoMigrationsPath, fileName),
                System.IO.Path.Combine(Path, fileName));
        }

        return this;
    }

    /// <summary>
    /// Copies a fixture script from this test project's <c>Fixtures/</c> folder
    /// and gives it the next free migration number.
    /// </summary>
    internal TempMigrations CopyFixture(string fixtureFileName, int number)
    {
        var source = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);
        var target = System.IO.Path.Combine(
            Path,
            number.ToString("0000", CultureInfo.InvariantCulture) + "_" + fixtureFileName);
        File.Copy(source, target);
        return this;
    }

    /// <inheritdoc />
    public void Dispose() => Root.Delete(recursive: true);
}

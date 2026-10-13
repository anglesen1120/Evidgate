// 06.3.2-T1.1 (AC1): the two generated fixtures, written under a temp directory the fixture owns so no build
// output and no repository byte carries them, and deleted when the test class that asked for them finishes.
namespace Tenancy.IntegrationTests;

/// <summary>
/// The generated half of the hostile corpus (AC1's <c>zipbomb.zip</c>/<c>oversized.bin</c>): one temp directory
/// holding both files, built once per test class as the xUnit class fixture and removed by <see cref="Dispose"/>.
/// The directory is deliberately outside the repository, so a running test never writes into <c>fixtures/</c> and
/// a failing run leaves no corpus bytes behind.
/// </summary>
public sealed class GeneratedHostileFixtures : IDisposable
{
    /// <summary>Builds both generated fixtures into a fresh temp directory.</summary>
    public GeneratedHostileFixtures()
    {
        DirectoryPath = Directory.CreateTempSubdirectory("evidgate-hostile-").FullName;

        File.WriteAllBytes(PathOf(GenerateBombFixture.FileName), GenerateBombFixture.Build());
        File.WriteAllBytes(PathOf(GenerateOversizedFixture.FileName), GenerateOversizedFixture.Build());
    }

    /// <summary>The temp directory holding both generated fixtures.</summary>
    public string DirectoryPath { get; }

    /// <summary>The absolute path of one generated fixture.</summary>
    /// <param name="file">The fixture file name (<see cref="GenerateBombFixture.FileName"/> or <see cref="GenerateOversizedFixture.FileName"/>).</param>
    public string PathOf(string file) => Path.Combine(DirectoryPath, file);

    /// <summary>
    /// Deletes the temp tree. Best effort: a leftover directory under the OS temp path never fails a run, and the
    /// names in it are the corpus's own.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // The tree is already gone or held open by another handle; the OS temp path owns it now.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a temp tree the process may not remove is not a corpus failure.
        }
    }
}

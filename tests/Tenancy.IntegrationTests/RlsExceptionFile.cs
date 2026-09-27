using Evidgate.BuildingBlocks.Persistence.Isolation;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The repository copy of <c>contracts/rls-exceptions.txt</c> (02.6.1-T1.4): the
/// list the CI lint of 02.6.1-T2 reads and the list the boot self-check embeds. The
/// path is derived from the fixture's own <c>migrations/</c> resolution (walk up to
/// <c>Evidgate.slnx</c>), so no test carries a second path convention.
/// </summary>
internal static class RlsExceptionFile
{
    /// <summary>The repository root, as the fixture resolved it.</summary>
    internal static string RepositoryRoot { get; } =
        Path.GetDirectoryName(PostgresTenancyFixture.RepoMigrationsPath)
        ?? throw new InvalidOperationException("the migrations path has no parent directory");

    /// <summary>Absolute path of <c>contracts/rls-exceptions.txt</c>.</summary>
    internal static string FullPath { get; } = Path.Combine(RepositoryRoot, RlsExceptionList.RelativePath);

    /// <summary>Parses the file as it is committed right now (never the embedded copy).</summary>
    internal static RlsExceptionList Read() => RlsExceptionList.Parse(File.ReadAllText(FullPath));
}

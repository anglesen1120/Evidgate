using System.Security.Cryptography;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.1.1-T1.4: the two identity migrations apply forward through the real
/// <c>Evidgate.Migrator</c> on real PostgreSQL 18, in order, journaled with their
/// checksums — and a deployment first brings the database to the previous head
/// (0003), so this is the real expand path, not a from-scratch run. A second run
/// applies nothing.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class MigrationTests
{
    private static readonly string[] IdentityMigrations =
    [
        "0004_identity_organization.sql",
        "0005_identity_project.sql",
    ];

    private readonly PostgresTenancyFixture _fixture;

    public MigrationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ApplyIdentityTenancy()
    {
        var database = await _fixture.CreateEmptyDatabaseAsync();

        // An existing database already at the foundation head: only the two new
        // files are pending, and 0005 (the FK to `organization`) runs after 0004.
        using var foundations = TempMigrationDirectory.CreateFrom(PostgresTenancyFixture.RepoMigrationsPath, "0001", "0002", "0003");

        var baselined = _fixture.ApplyMigrations(database, foundations.Path);
        baselined.ExitCode.ShouldBe(MigratorExitCodes.Success);
        baselined.Result.AppliedNow.ShouldBe(
        [
            "0001_foundation_roles.sql",
            "0002_foundation_evidgate_schema.sql",
            "0003_foundation_rls_probe.sql",
        ]);

        // Forward step bounded to the identity files, so later migrations (01.5.1
        // platform tables) never change what this test asserts.
        using var identity = TempMigrationDirectory.CreateFrom(
            PostgresTenancyFixture.RepoMigrationsPath, "0001", "0002", "0003", "0004", "0005");

        var applied = _fixture.ApplyMigrations(database, identity.Path);

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success);
        applied.Result.AppliedNow.ShouldBe(["0004_identity_organization.sql", "0005_identity_project.sql"]);

        // The plan computed before the run saw exactly the two new files pending on
        // the baselined database, and no drift in the foundation history.
        applied.Result.Plan!.Pending.Select(script => script.Name).ShouldBe(IdentityMigrations);
        applied.Result.Plan!.Drifted.ShouldBeEmpty();
        applied.Messages.ShouldContain(line =>
            line.StartsWith("summary: applied 2, failed 0", StringComparison.Ordinal));

        // The journal keeps one row per file, with the checksum of the bytes on disk.
        var journal = await _fixture.ReadJournalAsync(database);
        journal.Select(row => row.Name).ShouldBe(
        [
            "0001_foundation_roles.sql",
            "0002_foundation_evidgate_schema.sql",
            "0003_foundation_rls_probe.sql",
            "0004_identity_organization.sql",
            "0005_identity_project.sql",
        ]);

        foreach (var name in IdentityMigrations)
        {
            var row = journal.Single(entry => entry.Name == name);
            row.Checksum.ShouldBe(await FileChecksumAsync(name));
            row.DurationMs.ShouldNotBeNull();
            row.AppliedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-10));
        }

        // Forward-only: the applied order never goes backwards in time, so 0004
        // committed before 0005.
        journal.Select(row => row.AppliedAt).ShouldBe(journal.Select(row => row.AppliedAt).OrderBy(at => at));

        // The migration really built the registry: both tables, forced row security.
        (await _fixture.TableExistsAsync(database, "organization")).ShouldBeTrue();
        (await _fixture.TableExistsAsync(database, "project")).ShouldBeTrue();
        (await _fixture.RowSecurityAsync(database, "organization")).Forced.ShouldBeTrue();
        (await _fixture.RowSecurityAsync(database, "project")).Forced.ShouldBeTrue();

        // Re-run: nothing pending, nothing applied, same journal.
        var second = _fixture.ApplyMigrations(database, identity.Path);
        second.ExitCode.ShouldBe(MigratorExitCodes.Success);
        second.Result.AppliedNow.ShouldBeEmpty();
        second.Result.Plan!.Pending.ShouldBeEmpty();
        second.Messages.ShouldContain(line =>
            line.StartsWith("nothing to apply:", StringComparison.Ordinal));
        (await _fixture.ReadJournalAsync(database)).ShouldBe(journal);
    }

    private static async Task<string> FileChecksumAsync(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(
                Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, name),
                TestContext.Current.CancellationToken)));
}

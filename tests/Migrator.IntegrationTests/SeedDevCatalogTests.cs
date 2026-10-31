using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// A fresh dev stack has no catalog rows, so a manifest has no suite revision to name and a run upload cannot succeed.
/// The base seed now writes one case, one suite and one revision through the Catalog module's own service and commands;
/// these tests prove it happens once, reprinting the same ids and hash on a rerun, and that the refusals still cover it.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class SeedDevCatalogTests
{
    private readonly PostgresMigratorFixture _fixture;
    private readonly SeedDevTokenHarness _harness;

    public SeedDevCatalogTests(PostgresMigratorFixture fixture)
    {
        _fixture = fixture;
        _harness = new SeedDevTokenHarness(fixture);
    }

    [Fact]
    public async Task SeedDev_WithToken_AlsoSeedsTheCatalogSuite()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        // The documented walkthrough seeds with --with-token, so that mode must carry the catalog sample too.
        var run = _harness.RunWithToken(database, pepper);

        run.ExitCode.ShouldBe(MigratorExitCodes.Success, run.Error);
        ReadSeed(run.Output).Outcome.ShouldBe("created");
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(1, 1, 1, 1, 1));
    }

    [Fact]
    public async Task SeedDev_CatalogSuite_CreatedOnce_SameRevisionOnRerun()
    {
        var database = await _harness.MigratedDatabaseAsync();

        var first = RunSeed(database);
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);

        var seeded = ReadSeed(first.Output);
        seeded.Outcome.ShouldBe("created");

        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(1, 1, 1, 1, 1));
        (await RevisionAsync(database)).ShouldBe(seeded.Revision());
        (await _fixture.ScalarAsync<string>(database, "select case_revision_id::text from public.suite_member"))
            .ShouldBe(seeded.CaseRevisionId.ToString());

        // The rerun must add nothing and must name the same revision a manifest already references.
        var second = RunSeed(database);
        second.ExitCode.ShouldBe(MigratorExitCodes.Success, second.Error);
        second.Output.ShouldContain("(already present)");

        var again = ReadSeed(second.Output);
        again.Outcome.ShouldBe("already present");
        (again.SuiteRevisionId, again.CaseRevisionId, again.RevisionNo, again.RevisionHash)
            .ShouldBe((seeded.SuiteRevisionId, seeded.CaseRevisionId, seeded.RevisionNo, seeded.RevisionHash));
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(1, 1, 1, 1, 1));
        (await RevisionAsync(database)).ShouldBe(seeded.Revision());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public async Task SeedDev_CatalogSeed_OutsideDevelopment_Refuses_AndWritesNoCatalogRows(string? environment)
    {
        var database = await _harness.MigratedDatabaseAsync();

        var run = RunSeed(database, environment);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("refused");
        run.Output.ShouldNotContain(DevSeedCatalog.ManifestLine);
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task SeedDev_CatalogSeed_RemoteApplicationHost_Refuses_AndWritesNothing()
    {
        var database = await _harness.MigratedDatabaseAsync();
        var variables = _harness.Variables(
            database,
            pepper: string.Empty,
            environment: "Development",
            appConnectionString: "Host=db.internal.example.com;Port=5432;Database=evidgate;Username=evidgate_app;Password=x",
            omitPepper: true);

        var refused = SeedDevTokenHarness.Run(["seed-dev"], variables);

        refused.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        refused.Error.ShouldContain("not local");
        refused.Output.ShouldNotContain(DevSeedCatalog.ManifestLine);
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(0, 0, 0, 0, 0));

        // Positive control on the same database: only the application host differs from the refused run.
        var control = RunSeed(database);
        control.ExitCode.ShouldBe(MigratorExitCodes.Success, control.Error);
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(1, 1, 1, 1, 1));
    }

    private SeedDevTokenHarness.RunResult RunSeed(string database, string? environment = "Development") =>
        SeedDevTokenHarness.Run(
            ["seed-dev"],
            _harness.Variables(database, pepper: string.Empty, environment, omitPepper: true));

    /// <summary>The two lines the seed prints: the revision's head and the ids a manifest names.</summary>
    private static SeededRevision ReadSeed(string output)
    {
        var head = HeadLine().Match(output);
        head.Success.ShouldBeTrue($"the seed printed no suite revision line:\n{output}");

        var ids = ManifestIds().Match(output);
        ids.Success.ShouldBeTrue($"the seed printed no manifest inputs line:\n{output}");

        return new SeededRevision(
            Guid.Parse(ids.Groups["revision"].Value, CultureInfo.InvariantCulture),
            Guid.Parse(ids.Groups["case"].Value, CultureInfo.InvariantCulture),
            int.Parse(head.Groups["no"].Value, CultureInfo.InvariantCulture),
            head.Groups["hash"].Value,
            head.Groups["outcome"].Value);
    }

    private async Task<CatalogCounts> CatalogCountsAsync(string database) => new(
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.test_case"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.test_revision"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.suite"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.suite_revision"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.suite_member"));

    /// <summary>Identity, number, hash and slug of the one stored revision, as one comparable string.</summary>
    private async Task<string> RevisionAsync(string database) => await _fixture.ScalarAsync<string>(
        database,
        """
        select r.id::text || '|' || r.revision_no || '|' || r.revision_hash || '|' || s.slug
          from public.suite_revision r
          join public.suite s on s.id = r.suite_id
        """);

    [GeneratedRegex(
        @"revision (?<no>\d+) (?<hash>sha256:[0-9a-f]{64}) \((?<outcome>created|already present)\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex HeadLine();

    [GeneratedRegex(
        @"suite_revision_id=(?<revision>[0-9a-f-]{36}) case_revision_id=(?<case>[0-9a-f-]{36})",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex ManifestIds();

    private sealed record SeededRevision(
        Guid SuiteRevisionId,
        Guid CaseRevisionId,
        int RevisionNo,
        string RevisionHash,
        string Outcome)
    {
        internal string Revision() => $"{SuiteRevisionId}|{RevisionNo}|{RevisionHash}|{DevSeedCatalog.SuiteSlug}";
    }

    private sealed record CatalogCounts(long Cases, long CaseRevisions, long Suites, long SuiteRevisions, long Members);
}

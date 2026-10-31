using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.Migrator;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// A fresh dev stack has no catalog rows, so a manifest has no suite revision to name and a run upload cannot succeed.
/// The base seed now writes one case, one suite and one revision through the Catalog module's own service and commands,
/// and one environment revision through the Orchestrator command; these tests prove each happens once, reprinting the
/// same ids and hash on a rerun, and that the refusals still cover them.
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

    [Fact]
    public async Task SeedDev_CatalogSuite_NewerRevisionKeepsTheHead_AndTheRerunReportsAlreadyPresent()
    {
        var database = await _harness.MigratedDatabaseAsync();

        var first = RunSeed(database);
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);
        var seeded = ReadSeed(first.Output);

        // A later, different revision is the suite's current definition; the seed must leave it there.
        var newer = await CreateSecondRevisionAsync(database);
        newer.Created.ShouldBeTrue();
        newer.SuiteRevisionId.ShouldNotBe(seeded.SuiteRevisionId);
        newer.RevisionNo.ShouldBe(2);

        var rerun = RunSeed(database);

        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);
        rerun.Output.ShouldContain("(already present)");

        var again = ReadSeed(rerun.Output);
        again.Outcome.ShouldBe("already present");
        (again.SuiteRevisionId, again.RevisionNo).ShouldBe((seeded.SuiteRevisionId, seeded.RevisionNo));
        (await HeadAsync(database)).ShouldBe((newer.SuiteRevisionId, newer.RevisionNo));
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(1, 1, 1, 2, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task SeedDev_CatalogSeed_ExistingActiveCasesStayActive(int existing)
    {
        var database = await _harness.MigratedDatabaseAsync();

        // The tenant alone, as the seed's first half writes it, so the catalog half meets a project that already holds cases.
        var options = MigratorOptions.FromEnvironment(_harness.Variables(database, pepper: string.Empty, omitPepper: true));
        using var tenantOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var tenantError = new StringWriter(CultureInfo.InvariantCulture);
        DevSeed.WriteTenant(options, tenantOutput, tenantError).ShouldBe(MigratorExitCodes.Success, tenantError.ToString());

        await InsertActiveCasesAsync(database, existing);
        (await ActiveCasesAsync(database)).ShouldBe(existing);

        // The sample's registration is a one-file tree, so it would mark these missing: the seed must refuse instead.
        var run = RunSeed(database);

        run.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        run.Error.ShouldContain("already holds active cases");
        (await ActiveCasesAsync(database)).ShouldBe(existing);
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(existing, 0, 0, 0, 0));

        var rerun = RunSeed(database);

        rerun.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        rerun.Error.ShouldContain("already holds active cases");
        (await ActiveCasesAsync(database)).ShouldBe(existing);
    }

    [Fact]
    public async Task SeedDev_CatalogSeed_CaseCommittedBeforeTheSweep_IsRolledBackNotSwept()
    {
        var database = await _harness.MigratedDatabaseAsync();

        // Opens the race the plain read cannot close: the seed's guard passes on an empty project, then a sync commits
        // an unrelated active case before the registration counts. Holding the sync's advisory lock parks the seed
        // exactly there, between its guard and its sweep.
        await using var blocker = new NpgsqlConnection(SeedDevTokenHarness.Unpooled(_fixture.OwnerConnectionString(database)));
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await PostgresMigratorFixture.ExecuteAsync(
            blocker,
            $"select pg_advisory_lock(hashtextextended('catalog_sync:' || '{DevSeed.ProjectId:D}', 0))");

        var seed = Task.Run(() => RunSeed(database));
        var raced = Guid.CreateVersion7();
        try
        {
            await WaitForSyncLockWaitAsync(database);

            await using var writer = new NpgsqlConnection(SeedDevTokenHarness.Unpooled(_fixture.OwnerConnectionString(database)));
            await writer.OpenAsync(TestContext.Current.CancellationToken);
            await PostgresMigratorFixture.ExecuteAsync(
                writer,
                $"""
                insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, file_path, first_seen_commit)
                select '{raced}', o.id, p.id, '{Guid.CreateVersion7()}', 'raced dev case', 'playwright',
                       'tests/raced-dev-case.spec.ts', '0198a00000007000800000000000d0fe'
                from public.organization o join public.project p on p.org_id = o.id
                where o.id = '{DevSeed.OrgId}' and p.id = '{DevSeed.ProjectId}'
                """);
        }
        finally
        {
            await PostgresMigratorFixture.ExecuteAsync(blocker, "select pg_advisory_unlock_all()");
        }

        var run = await seed;

        // Registration reports the raced case as newly missing; the seed refuses and its transaction rolls the sweep
        // back, so the case keeps its status and nothing else is written.
        run.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        run.Error.ShouldContain("rolled back");
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(1, 0, 0, 0, 0));
        (await _fixture.ScalarAsync<string>(database, $"select status from public.test_case where id = '{raced}'")).ShouldBe("active");

        // Archiving the sync's case (cases are never deleted) leaves no active case, so the seed registers the sample
        // cleanly: nothing from the refused run survived.
        await using (var cleanup = new NpgsqlConnection(SeedDevTokenHarness.Unpooled(_fixture.OwnerConnectionString(database))))
        {
            await cleanup.OpenAsync(TestContext.Current.CancellationToken);
            await PostgresMigratorFixture.ExecuteAsync(
                cleanup,
                $"update public.test_case set status = 'archived' where id = '{raced}'");
        }

        var rerun = RunSeed(database);

        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(2, 1, 1, 1, 1));
    }

    /// <summary>Waits until a backend is queued on the catalog sync's advisory lock.</summary>
    private async Task WaitForSyncLockWaitAsync(string database)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_stat_activity where wait_event_type = 'Lock' and query like '%catalog_sync:%'");

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("the seed never queued on the catalog sync lock");
    }

    [Fact]
    public async Task SeedDev_EnvironmentRevision_CreatedOnceAndSameOnRerun()
    {
        var database = await _harness.MigratedDatabaseAsync();

        var first = RunSeed(database);
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);

        var seeded = ReadEnvironment(first.Output);
        seeded.Outcome.ShouldBe("created");
        (await EnvironmentCountsAsync(database)).ShouldBe((1, 1));
        (await _fixture.ScalarAsync<string>(database, "select base_urls->>'local' from public.environment_revision")).ShouldBe("http://127.0.0.1:8080");
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.environment_revision where cardinality(secret_refs) > 0")).ShouldBe(0);

        var rerun = RunSeed(database);
        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);

        var again = ReadEnvironment(rerun.Output);
        again.RevisionId.ShouldBe(seeded.RevisionId);
        again.RevisionNo.ShouldBe(seeded.RevisionNo);
        again.RevisionHash.ShouldBe(seeded.RevisionHash);
        again.Outcome.ShouldBe("already present");
        (await EnvironmentCountsAsync(database)).ShouldBe((1, 1));
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
        run.Output.ShouldNotContain(DevSeedEnvironment.ManifestLine);
        (await CatalogCountsAsync(database)).ShouldBe(new CatalogCounts(0, 0, 0, 0, 0));
        (await EnvironmentCountsAsync(database)).ShouldBe((0, 0));
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

    /// <summary>
    /// Creates a second suite revision of the dev suite through the Catalog module's own command, under the same
    /// synthetic principal the seed uses; empty members are legal, and their content differs from the sample's.
    /// </summary>
    private async Task<SuiteRevisionHead> CreateSecondRevisionAsync(string database)
    {
        var variables = _harness.Variables(database, pepper: string.Empty, omitPepper: true);
        await using var provider = DevSeedServices.Compose(variables, SeedDevTokenHarness.ContractsDirectory());
        using var tenantContext = provider.GetRequiredService<IRequestTenantContextSetter>()
            .Publish(DevSeed.OrgId, DevSeed.ProjectId);
        using var caller = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Member(DevSeed.UserId, DevSeed.UserEmail));
        await using var scope = provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var created = await mediator.Send(new CreateSuiteRevisionCommand(
            await _fixture.ScalarAsync<Guid>(database, "select id from public.suite"),
            new CreateSuiteRevisionRequest([], null, null, null, null),
            "dev-seed-catalog-test-second"));

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        return created.Value;
    }

    /// <summary>Writes <paramref name="count"/> active cases directly, the state a catalog sync would leave behind.</summary>
    private async Task InsertActiveCasesAsync(string database, int count)
    {
        var rows = string.Join(
            ",\n",
            Enumerable.Range(0, count).Select(index => string.Create(
                CultureInfo.InvariantCulture,
                $"('{Guid.CreateVersion7()}', '{DevSeed.OrgId}', '{DevSeed.ProjectId}', '{Guid.CreateVersion7()}', 'existing dev case {index}', 'playwright', 'tests/existing-dev-case-{index}.spec.ts', '0198a00000007000800000000000d0ff')")));

        await using var connection = new NpgsqlConnection(SeedDevTokenHarness.Unpooled(_fixture.OwnerConnectionString(database)));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PostgresMigratorFixture.ExecuteAsync(
            connection,
            "insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, file_path, first_seen_commit) values "
            + rows);
    }

    private async Task<long> ActiveCasesAsync(string database) => await _fixture.ScalarAsync<long>(
        database,
        "select count(*) from public.test_case where status = 'active'");

    /// <summary>The suite's head revision and its number, as one comparable pair.</summary>
    private async Task<(Guid? Id, int RevisionNo)> HeadAsync(string database) => (
        await _fixture.ScalarAsync<Guid?>(database, "select head_revision_id from public.suite"),
        await _fixture.ScalarAsync<int>(
            database,
            "select r.revision_no from public.suite s join public.suite_revision r on r.id = s.head_revision_id"));

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

    /// <summary>The environment revision the seed reports, from its own line and its manifest inputs line.</summary>
    private static SeededEnvironment ReadEnvironment(string output)
    {
        var line = EnvironmentLine().Match(output);
        line.Success.ShouldBeTrue($"the seed printed no environment revision line:\n{output}");

        var ids = EnvironmentManifestIds().Match(output);
        ids.Success.ShouldBeTrue($"the seed printed no environment revision id:\n{output}");

        return new SeededEnvironment(
            Guid.Parse(ids.Groups["revision"].Value, CultureInfo.InvariantCulture),
            int.Parse(line.Groups["no"].Value, CultureInfo.InvariantCulture),
            line.Groups["hash"].Value,
            line.Groups["outcome"].Value);
    }

    private async Task<(long Environments, long Revisions)> EnvironmentCountsAsync(string database) => (
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.environment"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.environment_revision"));

    /// <summary>Case samples, suites and environment revisions: the counts of one seed.</summary>
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
        @"environment dev revision (?<no>\d+) (?<hash>sha256:[0-9a-f]{64}) \((?<outcome>created|already present)\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex EnvironmentLine();

    [GeneratedRegex(
        @"environment_revision_id=(?<revision>[0-9a-f-]{36}) environment_key=dev",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex EnvironmentManifestIds();

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

    private sealed record SeededEnvironment(Guid RevisionId, int RevisionNo, string RevisionHash, string Outcome);
}

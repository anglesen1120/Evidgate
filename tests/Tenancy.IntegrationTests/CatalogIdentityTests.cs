using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 03.1.1 AC1–AC7 through the real host and a real PostgreSQL: the registration service (rename, conflicts, warnings,
/// missing), the database-level immutability, RLS, and the catalog routes with their 00 §9 envelope.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CatalogIdentityTests
{
    private const string IdA = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f";
    private const string IdB = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e60";
    private const string IdC = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e61";
    private const string Commit1 = "aaaaaaa1111111";
    private const string Commit2 = "bbbbbbb2222222";
    private const string Commit3 = "ccccccc3333333";

    private static readonly string[] AppendOnlyViolations =
    [
        "delete from public.test_case",
        "update public.case_identity_source set raw_key = 'x'",
        "delete from public.case_identity_source",
        "update public.case_rename_history set to_path = 'x'",
        "delete from public.case_rename_history",
    ];

    private static readonly string[] DuplicateLabels = ["smoke", "smoke", " p1 "];
    private static readonly int[] NotAnObject = [1, 2];

    private readonly PostgresTenancyFixture _fixture;

    public CatalogIdentityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.DisposeAsync();
            }
            finally
            {
                // The database is this world's to drop even when the host's own disposal throws.
                await Dedicated.DisposeAsync();
            }
        }
    }

    // Dedicated: the identity resolver's annotation lookup and the cross-tenant assertions read
    // rows no single tenant scope would own, so the class needs an empty database of its own.
    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, "acme");
            var project = await CreateProjectAsync(client, user, org, "checkout");

            return new World(api, client, database, user, org, project, dedicated);
        });

    private static SourceFile Annotated(string path, string id) => new(path, $"// evidgate.id: {id}\ntest('x', () => {{}});", "playwright", "typescript");

    private static async Task<CaseSyncReport> RegisterAsync(World world, string commit, params SourceFile[] files) =>
        await RegisterAsync(world.Api, world.Org, world.Project, commit, files);

    private static async Task<CaseSyncReport> RegisterAsync(TenancyApiFactory api, Guid org, Guid project, string commit, params SourceFile[] files)
    {
        await using var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(project, commit, files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return outcome.Report!;
    }

    private Task<long> CountAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{table}");

    // ------------------------------------------------------------------ AC1 identity survives a rename

    [Fact]
    public async Task Register_AnnotatedFile_CreatesOneCase_AndRecordsWhyItIsThatCase()
    {
        await using var world = await StartAsync();

        var report = await RegisterAsync(world, Commit1, Annotated("tests/login.spec.ts", IdA));

        (report.Registered, report.Renamed, report.Unchanged, report.Conflicts, report.Warnings, report.Missing).ShouldBe((1, 0, 0, 0, 0, 0));
        var entry = report.Entries.Single();
        (entry.Path, entry.Result).ShouldBe(("tests/login.spec.ts", CaseSyncResults.Registered));

        (await CountAsync(world, "test_case")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, "select annotation_id::text || '|' || file_path || '|' || status || '|' || first_seen_commit || '|' || framework from public.test_case"))
            .ShouldBe($"{IdA}|tests/login.spec.ts|active|{Commit1}|playwright");
        (await _fixture.ScalarAsync<string>(world.Database, "select source || '|' || commit_sha from public.case_identity_source")).ShouldBe($"annotation|{Commit1}");
    }

    [Fact]
    public async Task Register_RenameOnly_KeepsTheCase_AndWritesExactlyOneHistoryRow()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("tests/login.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var report = await RegisterAsync(world, Commit2, Annotated("tests/auth/login.spec.ts", IdA));

        (report.Registered, report.Renamed).ShouldBe((0, 1));
        report.Entries.Single().ShouldBe(new CaseSyncEntry("tests/auth/login.spec.ts", CaseSyncResults.Renamed, caseId));
        (await CountAsync(world, "test_case")).ShouldBe(1);
        (await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case")).ShouldBe(caseId);
        (await _fixture.ScalarAsync<string>(world.Database, "select file_path from public.test_case")).ShouldBe("tests/auth/login.spec.ts");
        (await _fixture.ScalarAsync<string>(world.Database, "select from_path || '>' || to_path || '@' || commit_sha from public.case_rename_history"))
            .ShouldBe($"tests/login.spec.ts>tests/auth/login.spec.ts@{Commit2}");
        (await CountAsync(world, "case_rename_history")).ShouldBe(1);
    }

    [Fact]
    public async Task Register_SameTreeAgain_WritesNothing_NotEvenAVersionBump()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        var before = await _fixture.ScalarAsync<string>(world.Database, "select string_agg(version::text || last_seen_commit, ',') from public.test_case");

        var report = await RegisterAsync(world, Commit2, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));

        (report.Registered, report.Renamed, report.Unchanged, report.Missing).ShouldBe((0, 0, 2, 0));
        report.Entries.ShouldBeEmpty();
        (await _fixture.ScalarAsync<string>(world.Database, "select string_agg(version::text || last_seen_commit, ',') from public.test_case")).ShouldBe(before);
        (await CountAsync(world, "case_identity_source")).ShouldBe(2);
    }

    [Fact]
    public async Task Register_ABatchOfRenamesAndNewCases_IsOneTransaction()
    {
        await using var world = await StartAsync();
        var first = Enumerable.Range(0, 50).Select(i => Annotated($"t/{i}.spec.ts", $"018f2f0e-8a52-7c4b-9d3e-{i:D12}")).ToArray();
        (await RegisterAsync(world, Commit1, first)).Registered.ShouldBe(50);

        var moved = first.Select((file, i) => i < 25 ? file with { Path = $"moved/{i}.spec.ts" } : file).ToArray();
        var report = await RegisterAsync(world, Commit2, moved);

        (report.Renamed, report.Unchanged, report.Missing).ShouldBe((25, 25, 0));
        (await CountAsync(world, "test_case")).ShouldBe(50);
        (await CountAsync(world, "case_rename_history")).ShouldBe(25);
    }

    // ------------------------------------------------------------------ AC2 duplicates

    [Fact]
    public async Task Register_DuplicateIdInOneCommit_ReportsEachPath_WritesNeitherFile_AndKeepsStoredState()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("tests/a.spec.ts", IdA), Annotated("tests/b.spec.ts", IdB));
        var before = await _fixture.ScalarAsync<string>(
            world.Database, "select string_agg(annotation_id::text || file_path || status || version::text, ',' order by annotation_id) from public.test_case");

        var report = await RegisterAsync(
            world,
            Commit2,
            Annotated("tests/a.spec.ts", IdA),
            Annotated("tests/copy-of-a.spec.ts", IdA),
            Annotated("tests/b.spec.ts", IdB));

        report.Conflicts.ShouldBe(2);
        report.Entries.Where(entry => entry.Code == CaseSyncCodes.DuplicateCaseId).Select(entry => entry.Path).ShouldBe(
            ["tests/a.spec.ts", "tests/copy-of-a.spec.ts"]);
        (report.Registered, report.Renamed, report.Missing).ShouldBe((0, 0, 0));

        // The stored case is untouched, neither moved nor marked missing, and nothing new was written.
        (await _fixture.ScalarAsync<string>(
            world.Database, "select string_agg(annotation_id::text || file_path || status || version::text, ',' order by annotation_id) from public.test_case")).ShouldBe(before);
        (await CountAsync(world, "case_rename_history")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC3 / AC4 sidecar

    [Fact]
    public async Task Register_SidecarEntry_IsAnEquivalentSource_RecordedAsSidecar()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    name: Checkout works\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        report.Registered.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, "select source || '|' || raw_key from public.case_identity_source")).ShouldBe("sidecar|checkout.spec.ts");
        (await _fixture.ScalarAsync<string>(world.Database, "select name from public.test_case")).ShouldBe("Checkout works");
    }

    [Fact]
    public async Task Register_AnnotationVersusSidecar_IsAConflict_WithNoCaseWritten()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile("evidgate.cases.yaml", $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdB}\n");

        var report = await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), sidecar);

        report.Conflicts.ShouldBe(1);
        report.Entries.Single().Code.ShouldBe(CaseSyncCodes.IdSourceConflict);
        (await CountAsync(world, "test_case")).ShouldBe(0);
        (await CountAsync(world, "case_identity_source")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC5 warnings

    [Fact]
    public async Task Register_MalformedSources_AreWarnings_AndTheOtherFilesStillRegister()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: ghost.spec.ts\n    evidgate.id: {IdC}\n");

        var report = await RegisterAsync(
            world,
            Commit1,
            new SourceFile("bad.spec.ts", "// evidgate.id: nope", "playwright"),
            Annotated("good.spec.ts", IdA),
            sidecar);

        report.Warnings.ShouldBe(2);
        report.Registered.ShouldBe(1);
        report.Entries.Where(entry => entry.Result == CaseSyncResults.Warning).Select(entry => (entry.Path, entry.Code)).ShouldBe(
        [
            ("bad.spec.ts", CaseSyncCodes.InvalidAnnotation),
            ("ghost.spec.ts", CaseSyncCodes.InvalidSidecarEntry),
        ]);
        (await _fixture.ScalarAsync<string>(world.Database, "select file_path from public.test_case")).ShouldBe("good.spec.ts");
    }

    // ------------------------------------------------------------------ §6 missing / returning / archived

    [Fact]
    public async Task Register_VanishedCase_BecomesMissing_AndComesBackActive()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));

        var gone = await RegisterAsync(world, Commit2, Annotated("a.spec.ts", IdA));
        gone.Missing.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, $"select status || '|' || last_seen_commit from public.test_case where annotation_id = '{IdB}'"))
            .ShouldBe($"missing|{Commit2}");

        var back = await RegisterAsync(world, Commit3, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        (back.Reactivated, back.Renamed, back.Missing).ShouldBe((1, 0, 0));
        (await _fixture.ScalarAsync<string>(world.Database, $"select status from public.test_case where annotation_id = '{IdB}'")).ShouldBe("active");
        (await CountAsync(world, "test_case")).ShouldBe(2);
    }

    [Fact]
    public async Task Register_ReturnAtANewPath_IsReactivatedAndRenamed()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("old.spec.ts", IdA), Annotated("stay.spec.ts", IdB));
        await RegisterAsync(world, Commit2, Annotated("stay.spec.ts", IdB));

        var report = await RegisterAsync(world, Commit3, Annotated("new.spec.ts", IdA), Annotated("stay.spec.ts", IdB));

        (report.Reactivated, report.Renamed).ShouldBe((1, 1));
        (await _fixture.ScalarAsync<string>(world.Database, $"select status || '|' || file_path from public.test_case where annotation_id = '{IdA}'")).ShouldBe("active|new.spec.ts");
    }

    [Fact]
    public async Task Register_AFileWithABrokenAnnotation_IsNotMarkedMissing()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));

        // The file is still there; its annotation is broken. The case must not flip to missing because of that.
        var report = await RegisterAsync(world, Commit2, new SourceFile("a.spec.ts", "// evidgate.id: broken", "playwright"));

        (report.Warnings, report.Missing).ShouldBe((1, 0));
        (await _fixture.ScalarAsync<string>(world.Database, "select status from public.test_case")).ShouldBe("active");
    }

    [Fact]
    public async Task Register_ArchivedCase_StaysArchived_WhenItsFileIsStillThere()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await _fixture.ExecuteAsync(world.Database, "update public.test_case set status = 'archived'");

        var report = await RegisterAsync(world, Commit2, Annotated("a.spec.ts", IdA));

        (report.Unchanged, report.Reactivated, report.Missing).ShouldBe((1, 0, 0));
        (await _fixture.ScalarAsync<string>(world.Database, "select status from public.test_case")).ShouldBe("archived");
    }

    [Fact]
    public async Task Register_AnEmptyTree_IsRefused_WhileTheProjectHasActiveCases()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));

        var outcome = await TryRegisterAsync(world, Commit2);

        (outcome.Succeeded, outcome.Failure).ShouldBe((false, "empty_tree"));
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.test_case where status = 'active'")).ShouldBe(2);

        // A project with nothing active can sync an empty tree.
        var empty = await StartAsync();
        await using var _ = empty;
        (await TryRegisterAsync(empty, Commit1)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Register_ATreeThatWouldMakeMostCasesMissing_IsRefused_AsTruncated()
    {
        await using var world = await StartAsync();
        var full = Enumerable.Range(0, 20).Select(i => Annotated($"t/{i}.spec.ts", $"018f2f0e-8a52-7c4b-9d3e-{i:D12}")).ToArray();
        await RegisterAsync(world, Commit1, full);

        var halfPlusOne = await TryRegisterAsync(world, Commit2, full[..9]);
        (halfPlusOne.Succeeded, halfPlusOne.Failure).ShouldBe((false, "incomplete_tree"));
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.test_case where status = 'active'")).ShouldBe(20);

        // Exactly half may vanish (a big cleanup is legitimate); under ten cases the guard does not apply.
        (await TryRegisterAsync(world, Commit2, full[..10])).Report!.Missing.ShouldBe(10);
        await using var small = await StartAsync();
        await RegisterAsync(small, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB), Annotated("c.spec.ts", IdC));
        (await TryRegisterAsync(small, Commit2, Annotated("a.spec.ts", IdA))).Report!.Missing.ShouldBe(2);
    }

    [Fact]
    public async Task Register_ARefusedSidecar_KeepsEveryCaseItIdentified_Active()
    {
        await using var world = await StartAsync();
        var good = new SourceFile("tests/evidgate.cases.yaml", $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n  - file: b.spec.ts\n    evidgate.id: {IdB}\n");
        var plain = new SourceFile("other/c.spec.ts", $"// evidgate.id: {IdC}", "playwright");
        await RegisterAsync(world, Commit1, new SourceFile("tests/a.spec.ts", "x", "playwright"), new SourceFile("tests/b.spec.ts", "x", "playwright"), good, plain);

        // Someone breaks the YAML: the files are still there but nothing vouches for their ids any more.
        var broken = good with { Content = "version: 2\ncases: []\n" };
        var report = await RegisterAsync(world, Commit2, new SourceFile("tests/a.spec.ts", "x", "playwright"), new SourceFile("tests/b.spec.ts", "x", "playwright"), broken, plain);

        (report.Warnings, report.Missing).ShouldBe((1, 0));
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.test_case where status = 'active'")).ShouldBe(3);
    }

    [Fact]
    public async Task Register_NamesAndPathsWithBrokenUnicode_AreWarnings_NotAFailedSync()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n    name: \"bad\\u0000name\"\n");

        var report = await RegisterAsync(
            world,
            Commit1,
            new SourceFile("a.spec.ts", "x", "playwright"),
            new SourceFile("b\u202Egnp.spec.ts", $"// evidgate.id: {IdB}", "playwright"),
            new SourceFile("c\uD800.spec.ts", $"// evidgate.id: {IdC}", "playwright"),
            sidecar);

        report.Registered.ShouldBe(0);
        report.Warnings.ShouldBeGreaterThanOrEqualTo(3);
        (await CountAsync(world, "test_case")).ShouldBe(0);
    }

    [Fact]
    public async Task Register_TwoSyncsOfOneProject_RunOneAfterTheOther_NoFailure()
    {
        await using var world = await StartAsync();
        var tree = Enumerable.Range(0, 200).Select(i => Annotated($"t/{i}.spec.ts", $"018f2f0e-8a52-7c4b-9d3e-{i:D12}")).ToArray();

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RegisterAsync(world, Commit1, tree)));

        runs.Sum(run => run.Registered).ShouldBe(200);
        runs.Count(run => run.Registered == 200).ShouldBe(1);
        (await CountAsync(world, "test_case")).ShouldBe(200);
        (await CountAsync(world, "test_revision")).ShouldBe(200);
    }

    private static async Task<CaseSyncOutcome> TryRegisterAsync(World world, string commit, params SourceFile[] files)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(world.Project, commit, files, TestContext.Current.CancellationToken);
        if (outcome.Succeeded)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return outcome;
    }

    [Fact]
    public async Task Register_UnknownProject_AndBadCommit_AreRefused()
    {
        await using var world = await StartAsync();
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var service = services.ServiceProvider.GetRequiredService<ICaseIdentityService>();

        var unknown = await service.RegisterDiscoveredAsync(Guid.CreateVersion7(), Commit1, [], TestContext.Current.CancellationToken);
        (unknown.Succeeded, unknown.Failure).ShouldBe((false, "project_not_found"));

        await Should.ThrowAsync<ArgumentException>(async () =>
            await service.RegisterDiscoveredAsync(world.Project, "NOT-HEX", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Register_TwoProjects_KeepSeparateIdentities_ForTheSameAnnotation()
    {
        await using var world = await StartAsync();
        var second = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");

        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, world.Org, second, Commit1, Annotated("a.spec.ts", IdA));

        (await CountAsync(world, "test_case")).ShouldBe(2);

        // Vanishing from one project's tree never touches the other project's cases.
        await RegisterAsync(world.Api, world.Org, second, Commit2, Annotated("other.spec.ts", IdB));
        (await _fixture.ScalarAsync<string>(world.Database, $"select status from public.test_case where project_id = '{world.Project}'")).ShouldBe("active");
    }

    // ------------------------------------------------------------------ AC6 immutability at the database

    [Fact]
    public async Task Database_RefusesToChangeIdentity_ForTheAppRole_AndForTheOwner()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));

        var changes = new List<string>
        {
            $"update public.test_case set annotation_id = '{IdB}'",
            "update public.test_case set first_seen_commit = '0000000'",
            $"update public.test_case set project_id = '{Guid.CreateVersion7()}'",
        };
        foreach (var change in changes)
        {
            var asApp = await Should.ThrowAsync<PostgresException>(async () => await AsAppAsync(world, change));
            asApp.SqlState.ShouldBe("23514");
            asApp.MessageText.ShouldContain("case_identity_immutable");

            var asOwner = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, change));
            asOwner.MessageText.ShouldContain("case_identity_immutable");
        }

        // Metadata stays writable and bumps the version through the shared trigger.
        await AsAppAsync(world, "update public.test_case set name = 'Renamed', file_path = 'z.spec.ts', labels = '{smoke}'");
        (await _fixture.ScalarAsync<string>(world.Database, "select name || '|' || file_path || '|' || version::text from public.test_case")).ShouldBe("Renamed|z.spec.ts|2");
    }

    [Fact]
    public async Task Database_CasesAreNeverDeleted_AndTheHistoryTablesAreAppendOnly()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world, Commit2, Annotated("b.spec.ts", IdA));

        foreach (var statement in AppendOnlyViolations)
        {
            var denied = await Should.ThrowAsync<PostgresException>(async () => await AsAppAsync(world, statement));
            denied.SqlState.ShouldBe("42501", statement);

            // The owner is stopped by the trigger even though its grants would allow the statement.
            var owner = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, statement));
            owner.MessageText.ShouldContain("case_identity_immutable", customMessage: statement);
        }
    }

    [Fact]
    public async Task Database_CompositeForeignKeys_RejectAnotherOrgsProject()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");

        var insert = $"insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) values ('{Guid.CreateVersion7()}', '{world.Org}', '{otherProject}', '{IdA}', 'x', 'pw', '{Commit1}')";
        var error = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, insert));

        error.ConstraintName.ShouldBe("test_case_project_fk");
    }

    // ------------------------------------------------------------------ RLS

    [Fact]
    public async Task Rls_AnotherOrgsCases_AreInvisible_AndAnUnsetTenantSeesNothing()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        await using (await scopes.BeginAsync(world.Org, null, true, TestContext.Current.CancellationToken))
        {
            (await db.QueryAsync<Guid>("select org_id from public.test_case", null, TestContext.Current.CancellationToken)).ShouldAllBe(id => id == world.Org);
            (await db.QueryAsync<Guid>("select org_id from public.case_identity_source", null, TestContext.Current.CancellationToken)).ShouldAllBe(id => id == world.Org);
        }

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from public.test_case", connection);
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC7 API

    private async Task<(Guid First, Guid Second, Guid Third)> SeedThreeAsync(World world)
    {
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        await Task.Delay(5, TestContext.Current.CancellationToken);
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB), Annotated("c.spec.ts", IdC));
        var ids = await IdsAsync(world);

        return (ids[0], ids[1], ids[2]);
    }

    private async Task<List<Guid>> IdsAsync(World world)
    {
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            ids.Add(await _fixture.ScalarAsync<Guid>(
                world.Database, $"select id from public.test_case order by created_at, id offset {index} limit 1"));
        }

        return ids;
    }

    /// <summary>Runs one statement as the app role inside a tenant transaction, so RLS shows the rows and the guards fire.</summary>
    private async Task AsAppAsync(World world, string sql)
    {
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task List_PagesInCreationOrder_WithACursor_AndFilters()
    {
        await using var world = await StartAsync();
        var (first, second, third) = await SeedThreeAsync(world);

        var page1 = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=2", world.Admin);
        page1.Status.ShouldBe(HttpStatusCode.OK);
        page1.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([first, second]);
        var cursor = page1.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNull();

        var page2 = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=2&cursor={Uri.EscapeDataString(cursor)}", world.Admin);
        page2.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldBe([third]);
        page2.Body.GetProperty("next_cursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set status = 'archived' where id = '{second}'");
        var live = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", world.Admin);
        live.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        var archived = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?status=archived", world.Admin);
        archived.Body.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(second);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?framework=jest", world.Admin))
            .Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task List_RefusesBadInput_AndHidesOtherTenantsProjects()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=0", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=201", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?status=bogus", world.Admin)).ErrorCode.ShouldBe("request_invalid");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?cursor=garbage", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Another org's project is the same 404 as a missing one.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{otherProject}/cases", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/cases", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_NeedsCaseRead_AndServiceIdentitiesDoNotHaveIt()
    {
        await using var world = await StartAsync();

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org)))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", _ => { })).Status.ShouldBe(HttpStatusCode.Unauthorized);

        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["case:read"]);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases", service)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_IsOneStatementPerPage_PlusTheProjectCheck()
    {
        await using var world = await StartAsync();
        await SeedThreeAsync(world);

        world.Api.Statements.Clear();
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/cases?limit=2", world.Admin)).Status.ShouldBe(HttpStatusCode.OK);

        var work = world.Api.Statements.Snapshot()
            .Where(statement => statement.Contains("public.", StringComparison.Ordinal) && statement.Contains("test_case", StringComparison.Ordinal))
            .ToList();
        work.Count.ShouldBe(1, string.Join(Environment.NewLine, world.Api.Statements.Snapshot()));
    }

    [Fact]
    public async Task Get_ReturnsTheCase_WithItsIdentityTrail_AndAnETag()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("old.spec.ts", IdA));
        await RegisterAsync(world, Commit2, Annotated("new.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        world.Api.Statements.Clear();
        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        var found = answer.Body.GetProperty("case");
        (found.GetProperty("annotation_id").GetGuid(), found.GetProperty("file_path").GetString(), found.GetProperty("status").GetString())
            .ShouldBe((Guid.Parse(IdA), "new.spec.ts", "active"));
        answer.Body.GetProperty("identity_sources").EnumerateArray().Single().GetProperty("source").GetString().ShouldBe("annotation");
        var rename = answer.Body.GetProperty("renames").EnumerateArray().Single();
        (rename.GetProperty("from_path").GetString(), rename.GetProperty("to_path").GetString()).ShouldBe(("old.spec.ts", "new.spec.ts"));
        answer.ETag.ShouldBe($"\"{found.GetProperty("version").GetInt32()}\"");

        world.Api.Statements.Snapshot().Count(statement => statement.Contains("from public.test_case", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Get_AnotherOrgsCase_IsNotFound_LikeAMissingOne()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));
        var theirs = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var cross = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{theirs}", world.Admin);
        var missing = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{Guid.CreateVersion7()}", world.Admin);

        cross.Status.ShouldBe(HttpStatusCode.NotFound);
        missing.Status.ShouldBe(HttpStatusCode.NotFound);
        cross.ErrorMessage.ShouldBe(missing.ErrorMessage);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{theirs}/revisions", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revisions_HoldTheOneVersionTheFirstSyncAppended()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions?limit=0", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Patch_ChangesMetadata_UnderIfMatch_AndAudits()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");
        var membership = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.membership where user_id = '{world.User}'");
        var etag = (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin)).ETag!;

        var patched = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"/v1/cases/{caseId}",
            world.Admin,
            new { name = "  Login works  ", owner_membership_id = membership, labels = DuplicateLabels },
            NewKey(),
            etag);

        patched.Status.ShouldBe(HttpStatusCode.OK, patched.ErrorMessage);
        patched.Text("name").ShouldBe("Login works");
        patched.Id("owner_membership_id").ShouldBe(membership);
        patched.Body.GetProperty("labels").EnumerateArray().Select(label => label.GetString()).ShouldBe(["smoke", "p1"]);
        patched.ETag.ShouldBe("\"2\"");
        patched.Text("annotation_id").ShouldBe(IdA);

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = 'case.update' and object_id = '{caseId}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_StaleIfMatch_Is412_WithTheCurrentVersion_AndMissingIfMatchIs422()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");
        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set name = 'changed elsewhere' where id = '{caseId}'");

        var stale = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, NewKey(), "\"1\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        stale.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Single(detail => detail.GetProperty("path").GetString() == "version").GetProperty("reason").GetString().ShouldBe("2");

        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, NewKey())).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, NewKey(), "abc")).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "mine" }, null, "\"2\"")).Status
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("annotation_id")]
    [InlineData("file_path")]
    [InlineData("first_seen_commit")]
    [InlineData("project_id")]
    [InlineData("id")]
    public async Task Patch_IdentityFields_Are422_CaseFieldImmutable_AndNothingChanges(string field)
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var body = new Dictionary<string, object?> { ["name"] = "sneaky", [field] = IdB };
        var answer = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, body, NewKey(), "\"1\"");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("case_field_immutable");
        (await _fixture.ScalarAsync<string>(world.Database, "select name || version::text from public.test_case")).ShouldBe("a.spec1");
    }

    [Fact]
    public async Task Patch_RefusesUnknownMembers_BadShapes_AndBadValues()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        async Task<ApiAnswer> Patch(object body) =>
            await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, body, NewKey(), "\"1\"");

        (await Patch(new { colour = "red" })).ErrorCode.ShouldBe("request_invalid");
        (await Patch(NotAnObject)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { name = "   " })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { name = new string('x', 513) })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { status = "missing" })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { labels = Enumerable.Range(0, 33).Select(i => "l" + i).ToArray() })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { labels = new[] { new string('x', 65) } })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { owner_membership_id = Guid.Empty })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Patch(new { name = 5 })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var stranger = await Patch(new { owner_membership_id = Guid.CreateVersion7() });
        stranger.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        stranger.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe("owner_membership_id");
        (await _fixture.ScalarAsync<int>(world.Database, "select version from public.test_case")).ShouldBe(1);
    }

    [Fact]
    public async Task Patch_StatusMoves_FollowTheStateMachine()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        async Task<ApiAnswer> Move(string status, int version) =>
            await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { status }, NewKey(), $"\"{version}\"");

        (await Move("active", 1)).Status.ShouldBe(HttpStatusCode.OK);
        (await Move("archived", 1)).Text("status").ShouldBe("archived");
        var again = await Move("archived", 2);
        again.Status.ShouldBe(HttpStatusCode.OK);
        (await Move("active", 2)).Text("status").ShouldBe("active");

        // active -> missing is the system's move; a missing case can be archived but not "reactivated" by hand.
        await _fixture.ExecuteAsync(world.Database, "update public.test_case set status = 'missing'");
        var version = await _fixture.ScalarAsync<int>(world.Database, "select version from public.test_case");
        var blocked = await Move("active", version);
        blocked.Status.ShouldBe(HttpStatusCode.Conflict);
        blocked.ErrorCode.ShouldBe("invalid_transition");
        (await Move("archived", version)).Text("status").ShouldBe("archived");
    }

    [Fact]
    public async Task Patch_NeedsCaseWrite_AndAnotherOrgsCaseIs404()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));
        var mine = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_case where org_id = '{world.Org}'");
        var theirs = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_case where org_id = '{otherOrg}'");

        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{mine}", stranger, new { name = "x" }, NewKey(), "\"1\"")).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{theirs}", world.Admin, new { name = "x" }, NewKey(), "\"1\"")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<string>(world.Database, $"select name from public.test_case where id = '{theirs}'")).ShouldBe("a.spec");
    }

    [Fact]
    public async Task Patch_IsIdempotent_ByKey()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");
        var key = NewKey();

        var first = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "Once" }, key, "\"1\"");
        var replay = await world.Client.SendAsync(HttpMethod.Patch, $"/v1/cases/{caseId}", world.Admin, new { name = "Once" }, key, "\"1\"");

        first.Status.ShouldBe(HttpStatusCode.OK);
        replay.Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<int>(world.Database, "select version from public.test_case")).ShouldBe(2);
    }

    [Fact]
    public async Task Patch_BodyThatIsNotJson_Is400_AndOneOverSixteenKiB_Is413()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        async Task<HttpStatusCode> Send(string text)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/cases/{caseId}") { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
            world.Admin(request.Headers);
            request.Headers.Add("Idempotency-Key", NewKey());
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);

            return response.StatusCode;
        }

        (await Send("{not json")).ShouldBe(HttpStatusCode.BadRequest);
        (await Send("{\"name\":\"" + new string('x', 16 * 1024) + "\"}")).ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // ------------------------------------------------------------------ requirement pointer (03.1.4)

    [Fact]
    public async Task RequirementRef_IsNullByDefault_IsTrimmed_AndRejectsOver128Chars()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var initial = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin);
        initial.Status.ShouldBe(HttpStatusCode.OK);
        initial.Body.GetProperty("case").GetProperty("requirement_ref").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _fixture.ScalarAsync<string?>(world.Database, "select requirement_ref from public.test_case")).ShouldBeNull();

        var padded = await PatchRequirementAsync(world, caseId, new { requirement_ref = "  REQ-1042  " });
        padded.Status.ShouldBe(HttpStatusCode.OK);
        padded.Body.GetProperty("requirement_ref").GetString().ShouldBe("REQ-1042");
        padded.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case")).ShouldBe("REQ-1042");

        // 128 characters is the limit (D-A6), and it is measured after trimming.
        var longest = await PatchRequirementAsync(world, caseId, new { requirement_ref = " " + new string('r', 128) + " " });
        longest.Status.ShouldBe(HttpStatusCode.OK);
        longest.Body.GetProperty("requirement_ref").GetString().ShouldBe(new string('r', 128));

        // A full requirement URL survives the character set, but a query string does not: '?' is not a member.
        var url = await PatchRequirementAsync(
            world, caseId, new { requirement_ref = "https://jira.example.com/browse/PROJ-1#criteria" });
        url.Status.ShouldBe(HttpStatusCode.OK);
        url.Body.GetProperty("requirement_ref").GetString().ShouldBe("https://jira.example.com/browse/PROJ-1#criteria");

        var tooLong = await PatchRequirementAsync(world, caseId, new { requirement_ref = new string('r', 129) });
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The envelope's top-level code is the shared 422 `request_invalid` (00 §9.2); the field-level verdict lives
        // in `details[]`, exactly as every other validation failure in this API reports itself.
        tooLong.ErrorCode.ShouldBe("request_invalid");
        var tooLongDetail = tooLong.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        tooLongDetail.GetProperty("path").GetString().ShouldBe("requirement_ref");
        tooLongDetail.GetProperty("reason").GetString().ShouldBe("invalid_length");

        // The refused value changed nothing, so the accepted one is still stored.
        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case"))
            .ShouldBe("https://jira.example.com/browse/PROJ-1#criteria");
        (await _fixture.ScalarAsync<int>(world.Database, "select version from public.test_case")).ShouldBe(4);
    }

    [Theory]
    // Blank trims to nothing, so it is a length refusal; every other row holds a character the set does not carry
    // (or, for a lone surrogate, not text at all), which is a value refusal.
    [InlineData("   ", "invalid_length")]
    [InlineData("REQ 1042", "invalid_value")]
    [InlineData("https://jira.example.com/browse/PROJ-1?focused=1", "invalid_value")]
    [InlineData("REQ\u0001", "invalid_value")]
    [InlineData("REQ\u200b", "invalid_value")]
    [InlineData("REQ\n1042", "invalid_value")]
    [InlineData("\tREQ-1", "invalid_value")]
    [InlineData("REQ-1\n", "invalid_value")]
    [InlineData("\uD800", "invalid_value")]
    [InlineData("REQ\uD800", "invalid_value")]
    public async Task RequirementRef_RefusesBlankCharsetAndControlBearingValues(string value, string reason)
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var answer = await PatchRequirementAsync(world, caseId, new { requirement_ref = value });

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single()
            .GetProperty("reason").GetString().ShouldBe(reason);
        (await _fixture.ScalarAsync<string?>(world.Database, "select requirement_ref from public.test_case")).ShouldBeNull();
        (await _fixture.ScalarAsync<int>(world.Database, "select version from public.test_case")).ShouldBe(1);
    }

    [Fact]
    public async Task RequirementRef_RefusesAbsentNonNullStringAndExtraMembers()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var absent = await PatchRequirementAsync(world, caseId, new { });
        absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        absent.ErrorCode.ShouldBe("request_invalid");
        absent.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Single().GetProperty("reason").GetString().ShouldBe("required");

        var notAString = await PatchRequirementAsync(world, caseId, new { requirement_ref = 42 });
        notAString.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        notAString.ErrorCode.ShouldBe("request_invalid");

        var extra = await PatchRequirementAsync(world, caseId, new { requirement_ref = "REQ-1", name = "sneaky" });
        extra.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        extra.ErrorCode.ShouldBe("request_invalid");
        extra.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Single().GetProperty("path").GetString().ShouldBe("name");

        // The name the extra member tried to change is untouched: this route writes the pointer only.
        (await _fixture.ScalarAsync<string>(world.Database, "select name from public.test_case")).ShouldBe("a.spec");
        (await _fixture.ScalarAsync<string?>(world.Database, "select requirement_ref from public.test_case")).ShouldBeNull();
    }

    [Fact]
    public async Task RequirementRefChange_IsMetadataOnly_NoRevisionNoHashChange()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");
        var before = await _fixture.ScalarAsync<string>(
            world.Database,
            "select string_agg(revision_hash || '|' || assertion_hash || '|' || content_sha256, ',' order by created_at) from public.test_revision");
        var revisions = await CountAsync(world, "test_revision");
        revisions.ShouldBeGreaterThan(0);

        var set = await PatchRequirementAsync(world, caseId, new { requirement_ref = "REQ-1042" });
        set.Status.ShouldBe(HttpStatusCode.OK);

        // Replaying the same value is a no-op, and clearing it is another metadata write.
        (await PatchRequirementAsync(world, caseId, new { requirement_ref = "REQ-1042" })).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<int>(world.Database, "select version from public.test_case")).ShouldBe(2);
        var cleared = await PatchRequirementAsync(world, caseId, new { requirement_ref = (string?)null });
        cleared.Status.ShouldBe(HttpStatusCode.OK);
        cleared.Body.GetProperty("requirement_ref").ValueKind.ShouldBe(JsonValueKind.Null);

        (await CountAsync(world, "test_revision")).ShouldBe(revisions);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select string_agg(revision_hash || '|' || assertion_hash || '|' || content_sha256, ',' order by created_at) from public.test_revision"))
            .ShouldBe(before);
        (await _fixture.ScalarAsync<string?>(world.Database, "select requirement_ref from public.test_case")).ShouldBeNull();
    }

    [Fact]
    public async Task RequirementRefPatch_NeedsCaseWrite_ForeignProjectAndOrgAre404()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, "returns");
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));
        var mine = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_case where org_id = '{world.Org}'");
        var theirs = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_case where org_id = '{otherOrg}'");

        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{mine}", stranger, new { requirement_ref = "REQ-1" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{theirs}", world.Admin, new { requirement_ref = "REQ-1" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);

        // Same org, other project: the route's project scopes the case, so this is a 404 and never a write.
        var wrongProject = await world.Client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{siblingProject}/cases/{mine}", world.Admin, new { requirement_ref = "REQ-1" }, NewKey());
        wrongProject.Status.ShouldBe(HttpStatusCode.NotFound);
        wrongProject.ErrorCode.ShouldBe("not_found");

        (await _fixture.ScalarAsync<string?>(world.Database, $"select requirement_ref from public.test_case where id = '{mine}'")).ShouldBeNull();
        (await _fixture.ScalarAsync<string?>(world.Database, $"select requirement_ref from public.test_case where id = '{theirs}'")).ShouldBeNull();
        (await _fixture.ScalarAsync<int>(world.Database, $"select version from public.test_case where id = '{mine}'")).ShouldBe(1);

        // The owner still can, and the other org's row stays invisible to every read of this tenant.
        var stored = await PatchRequirementAsync(world, mine, new { requirement_ref = "REQ-1" });
        stored.Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{theirs}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<string>(world.Database, $"select requirement_ref from public.test_case where id = '{theirs}'"))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Register_SidecarRequirement_IsStoredOnce_AndALaterSyncNeverRewritesIt()
    {
        await using var world = await StartAsync();
        SourceFile Sidecar(string? requirement) =>
            new(
                "tests/evidgate.cases.yaml",
                "version: 1\ncases:\n"
                + $"  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n"
                + (requirement is null ? string.Empty : $"    requirement: {requirement}\n"));

        var source = new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript");
        await RegisterAsync(world, Commit1, source, Sidecar("\" REQ-1042 \""));

        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case")).ShouldBe("REQ-1042");

        // A later sync with another value, and then with none, leaves the stored pointer alone: after
        // registration the explicit route is its only writer, so a sidecar edit cannot silently re-point a case.
        await RegisterAsync(world, Commit2, source, Sidecar("REQ-9999"));
        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case")).ShouldBe("REQ-1042");

        await RegisterAsync(world, Commit3, source, Sidecar(null));
        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case")).ShouldBe("REQ-1042");
        (await CountAsync(world, "test_case")).ShouldBe(1);
    }

    [Fact]
    public async Task Register_SidecarBooleanRequirement_IsAWarning_AndTheCaseStillRegisters()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    requirement: true\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        // A bare true/false is a scalar the schema accepts but no requirement id: warning, no conflict, id kept.
        // The report carries both entries for this one file: the warning about its dropped pointer and the
        // registration that still gave it its id.
        report.Warnings.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Registered.ShouldBe(1);
        report.Entries.Single(entry => entry.Result == CaseSyncResults.Warning).Code.ShouldBe(CaseSyncCodes.InvalidSidecarEntry);
        (await _fixture.ScalarAsync<string?>(world.Database, "select requirement_ref from public.test_case")).ShouldBeNull();
        (await CountAsync(world, "test_case")).ShouldBe(1);
    }

    [Fact]
    public async Task Register_SidecarNumericRequirement_IsStoredAsItsDigits()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    requirement: 1042\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        report.Warnings.ShouldBe(0);
        report.Registered.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case")).ShouldBe("1042");
    }

    [Theory]
    [InlineData("REQ 1042")]
    [InlineData("REQ?focused=1")]
    [InlineData("   ")]
    [InlineData("REQ-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Register_SidecarRequirementOutsideTheRule_WarnsOnThatEntry_AndBothCasesAreWritten(string requirement)
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n"
            + $"  - file: good.spec.ts\n    evidgate.id: {IdA}\n    requirement: REQ-1042\n"
            + $"  - file: bad.spec.ts\n    evidgate.id: {IdB}\n    requirement: {JsonSerializer.Serialize(requirement)}\n");

        var report = await RegisterAsync(
            world,
            Commit1,
            new SourceFile("tests/good.spec.ts", "test('x')", "playwright", "typescript"),
            new SourceFile("tests/bad.spec.ts", "test('y')", "playwright", "typescript"),
            sidecar);

        // One unusable pointer is metadata about one entry: it warns there and neither case loses its id, because
        // document validation runs before per-entry semantics and keeps the value's shape unconstrained.
        report.Warnings.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Registered.ShouldBe(2);
        report.Entries.Single(entry => entry.Result == CaseSyncResults.Warning).Path.ShouldBe("tests/bad.spec.ts");
        (await CountAsync(world, "test_case")).ShouldBe(2);
        (await _fixture.ScalarAsync<string>(world.Database, "select requirement_ref from public.test_case where file_path = 'tests/good.spec.ts'")).ShouldBe("REQ-1042");
        (await _fixture.ScalarAsync<string?>(world.Database, "select requirement_ref from public.test_case where file_path = 'tests/bad.spec.ts'")).ShouldBeNull();
    }

    [Fact]
    public async Task Register_SidecarRequirementInMalformedManifest_IsAWarning_AndNoCaseIsWritten()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    requirement: \"REQ-1\"\n  - file: [\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        report.Warnings.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Registered.ShouldBe(0);
        (await CountAsync(world, "test_case")).ShouldBe(0);
        var answer = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{world.Project}/cases", world.Admin);
        answer.Status.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<ApiAnswer> PatchRequirementAsync(World world, Guid caseId, object body) =>
        world.Client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{caseId}", world.Admin, body, NewKey());
}

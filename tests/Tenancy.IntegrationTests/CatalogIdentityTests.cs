using System.Net;
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
[Collection(TenancySuite.Name)]
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

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

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
        await RegisterAsync(world, Commit1, Annotated("old.spec.ts", IdA));
        await RegisterAsync(world, Commit2);

        var report = await RegisterAsync(world, Commit3, Annotated("new.spec.ts", IdA));

        (report.Reactivated, report.Renamed).ShouldBe((1, 1));
        (await _fixture.ScalarAsync<string>(world.Database, "select status || '|' || file_path from public.test_case")).ShouldBe("active|new.spec.ts");
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
    public async Task Register_AnEmptyTree_MarksEveryActiveCaseMissing_AndLeavesArchivedAlone()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set status = 'archived' where annotation_id = '{IdB}'");

        var report = await RegisterAsync(world, Commit2);

        report.Missing.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, $"select status from public.test_case where annotation_id = '{IdB}'")).ShouldBe("archived");
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
        await RegisterAsync(world.Api, world.Org, second, Commit2);
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
    public async Task Revisions_AreEmpty_UntilTheFirstSyncAppendsOne()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
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
}

using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogIdentityTests
{
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
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select annotation_id::text || '|' || file_path || '|' || status || '|' || first_seen_commit || '|' || framework from public.test_case")))
            .ShouldBe($"{IdA}|tests/login.spec.ts|active|{Commit1}|playwright");
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select source || '|' || commit_sha from public.case_identity_source"))).ShouldBe($"annotation|{Commit1}");
    }

    [Fact]
    public async Task Register_RenameOnly_KeepsTheCase_AndWritesExactlyOneHistoryRow()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("tests/login.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        var report = await RegisterAsync(world, Commit2, Annotated("tests/auth/login.spec.ts", IdA));

        (report.Registered, report.Renamed).ShouldBe((0, 1));
        report.Entries.Single().ShouldBe(new CaseSyncEntry("tests/auth/login.spec.ts", CaseSyncResults.Renamed, caseId));
        (await CountAsync(world, "test_case")).ShouldBe(1);
        (await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"))).ShouldBe(caseId);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select file_path from public.test_case"))).ShouldBe("tests/auth/login.spec.ts");
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select from_path || '>' || to_path || '@' || commit_sha from public.case_rename_history")))
            .ShouldBe($"tests/login.spec.ts>tests/auth/login.spec.ts@{Commit2}");
        (await CountAsync(world, "case_rename_history")).ShouldBe(1);
    }

    [Fact]
    public async Task Register_SameTreeAgain_WritesNothing_NotEvenAVersionBump()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        var before = await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select string_agg(version::text || last_seen_commit, ',') from public.test_case"));

        var report = await RegisterAsync(world, Commit2, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));

        (report.Registered, report.Renamed, report.Unchanged, report.Missing).ShouldBe((0, 0, 2, 0));
        report.Entries.ShouldBeEmpty();
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select string_agg(version::text || last_seen_commit, ',') from public.test_case"))).ShouldBe(before);
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
            world.Database, Scoped(world, "select string_agg(annotation_id::text || file_path || status || version::text, ',' order by annotation_id) from public.test_case"));

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
            world.Database, Scoped(world, "select string_agg(annotation_id::text || file_path || status || version::text, ',' order by annotation_id) from public.test_case"))).ShouldBe(before);
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
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select source || '|' || raw_key from public.case_identity_source"))).ShouldBe("sidecar|checkout.spec.ts");
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select name from public.test_case"))).ShouldBe("Checkout works");
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
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select file_path from public.test_case"))).ShouldBe("good.spec.ts");
    }

    // ------------------------------------------------------------------ §6 missing / returning / archived

    [Fact]
    public async Task Register_VanishedCase_BecomesMissing_AndComesBackActive()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));

        var gone = await RegisterAsync(world, Commit2, Annotated("a.spec.ts", IdA));
        gone.Missing.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select status || '|' || last_seen_commit from public.test_case", $"annotation_id = '{IdB}'")))
            .ShouldBe($"missing|{Commit2}");

        var back = await RegisterAsync(world, Commit3, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));
        (back.Reactivated, back.Renamed, back.Missing).ShouldBe((1, 0, 0));
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select status from public.test_case", $"annotation_id = '{IdB}'"))).ShouldBe("active");
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
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select status || '|' || file_path from public.test_case", $"annotation_id = '{IdA}'"))).ShouldBe("active|new.spec.ts");
    }

    [Fact]
    public async Task Register_AFileWithABrokenAnnotation_IsNotMarkedMissing()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));

        // The file is still there; its annotation is broken. The case must not flip to missing because of that.
        var report = await RegisterAsync(world, Commit2, new SourceFile("a.spec.ts", "// evidgate.id: broken", "playwright"));

        (report.Warnings, report.Missing).ShouldBe((1, 0));
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select status from public.test_case"))).ShouldBe("active");
    }

    [Fact]
    public async Task Register_ArchivedCase_StaysArchived_WhenItsFileIsStillThere()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await _fixture.ExecuteAsync(world.Database, $"update public.test_case set status = 'archived' where org_id = '{world.Org}'");

        var report = await RegisterAsync(world, Commit2, Annotated("a.spec.ts", IdA));

        (report.Unchanged, report.Reactivated, report.Missing).ShouldBe((1, 0, 0));
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select status from public.test_case"))).ShouldBe("archived");
    }

    [Fact]
    public async Task Register_AnEmptyTree_IsRefused_WhileTheProjectHasActiveCases()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA), Annotated("b.spec.ts", IdB));

        var outcome = await TryRegisterAsync(world, Commit2);

        (outcome.Succeeded, outcome.Failure).ShouldBe((false, "empty_tree"));
        (await _fixture.ScalarAsync<long>(world.Database, Scoped(world, "select count(*) from public.test_case", "status = 'active'"))).ShouldBe(2);

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
        (await _fixture.ScalarAsync<long>(world.Database, Scoped(world, "select count(*) from public.test_case", "status = 'active'"))).ShouldBe(20);

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
        (await _fixture.ScalarAsync<long>(world.Database, Scoped(world, "select count(*) from public.test_case", "status = 'active'"))).ShouldBe(3);
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
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select status from public.test_case", $"project_id = '{world.Project}'"))).ShouldBe("active");
    }

    // ------------------------------------------------------------------ AC6 immutability at the database

    [Fact]
    public async Task Database_RefusesToChangeIdentity_ForTheAppRole_AndForTheOwner()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));

        var changes = new List<string>
        {
            $"update public.test_case set annotation_id = '{IdB}' where org_id = '{world.Org}'",
            $"update public.test_case set first_seen_commit = '0000000' where org_id = '{world.Org}'",
            $"update public.test_case set project_id = '{Guid.CreateVersion7()}' where org_id = '{world.Org}'",
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
        await AsAppAsync(world, $"update public.test_case set name = 'Renamed', file_path = 'z.spec.ts', labels = '{{smoke}}' where org_id = '{world.Org}'");
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select name || '|' || file_path || '|' || version::text from public.test_case"))).ShouldBe("Renamed|z.spec.ts|2");
    }

    [Fact]
    public async Task Database_CasesAreNeverDeleted_AndTheHistoryTablesAreAppendOnly()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world, Commit2, Annotated("b.spec.ts", IdA));

        foreach (var statement in AppendOnlyViolations(world))
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
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
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
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));

        // The class shares one database, so these raw checks cover exactly the two orgs this test created;
        // RLS must still hide the other org's rows from world.Org, and everything from an unset tenant.
        var createdOrgs = $"('{world.Org}', '{otherOrg}')";
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        await using (await scopes.BeginAsync(world.Org, null, true, TestContext.Current.CancellationToken))
        {
            (await db.QueryAsync<Guid>($"select org_id from public.test_case where org_id in {createdOrgs}", null, TestContext.Current.CancellationToken)).ShouldAllBe(id => id == world.Org);
            (await db.QueryAsync<Guid>($"select org_id from public.case_identity_source where org_id in {createdOrgs}", null, TestContext.Current.CancellationToken)).ShouldAllBe(id => id == world.Org);
        }

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"select count(*) from public.test_case where org_id in {createdOrgs}", connection);
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0);
    }
}

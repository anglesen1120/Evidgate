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
/// 03.1.3 AC1–AC5 through the real host and a real PostgreSQL: append-only revisions keyed by content hash, reuse across
/// renames and re-syncs, immutability at the database, the store contract and the revisions route.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class CatalogRevisionTests
{
    private const string IdA = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f";
    private const string IdB = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e60";
    private const string Commit1 = "aaaaaaa1111111";
    private const string Commit2 = "bbbbbbb2222222";
    private const string Commit3 = "ccccccc3333333";
    private const string Commit4 = "ddddddd4444444";

    private readonly PostgresTenancyFixture _fixture;

    public CatalogRevisionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
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

    private static SourceFile Test(string path, string id, string body = "expect(a).toBe(1);") =>
        new(path, $"// evidgate.id: {id}\ntest('x', async () => {{ {body} }});", "playwright", "typescript");

    private static async Task<CaseSyncReport> RegisterAsync(World world, string commit, params SourceFile[] files)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(world.Project, commit, files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return outcome.Report!;
    }

    private Task<long> RevisionCount(World world) => _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.test_revision");

    // ------------------------------------------------------------------ AC1 / AC3

    [Fact]
    public async Task Sync_StoresOneRevision_PerCase_WithHashesAndNoSource()
    {
        await using var world = await StartAsync();

        var report = await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA, "const password = 'hunter2'; expect(a).toBe(1);"));

        report.Revisions.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            "select commit_sha || '|' || file_path || '|' || framework || '|' || language || '|' || (metadata->>'assertion_count') || '|' || (metadata->>'assertions_extracted') from public.test_revision"))
            .ShouldBe($"{Commit1}|a.spec.ts|playwright|typescript|1|true");
        (await _fixture.ScalarAsync<string>(world.Database, "select revision_hash || content_sha256 || assertion_hash || metadata::text from public.test_revision"))
            .ShouldNotContain("hunter2");

        // No column can hold the source (S3).
        (await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from information_schema.columns where table_name = 'test_revision' and column_name in ('content', 'source', 'body', 'text')"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Sync_OfAnUnchangedTree_InsertsNoRevision()
    {
        await using var world = await StartAsync();
        var tree = Enumerable.Range(0, 300).Select(i => Test($"t/{i}.spec.ts", $"018f2f0e-8a52-7c4b-9d3e-{i:D12}")).ToArray();
        (await RegisterAsync(world, Commit1, tree)).Revisions.ShouldBe(300);

        world.Api.Statements.Clear();
        var again = await RegisterAsync(world, Commit2, tree);

        again.Revisions.ShouldBe(0);
        again.Unchanged.ShouldBe(300);
        (await RevisionCount(world)).ShouldBe(300);

        // One bulk statement for 300 files, not one per file.
        world.Api.Statements.Snapshot().Count(statement => statement.Contains("insert into public.test_revision", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Rename_OnlyCommit_ReusesTheRevision_AndKeepsTheFirstPath()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("old/a.spec.ts", IdA));

        var report = await RegisterAsync(world, Commit2, Test("new/a.spec.ts", IdA));

        (report.Renamed, report.Revisions).ShouldBe((1, 0));
        (await RevisionCount(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, "select file_path from public.test_revision")).ShouldBe("old/a.spec.ts");
    }

    [Fact]
    public async Task ChangedContent_IsANewRevision_AndRevertingReusesTheOldRow()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA, "expect(a).toBe(1);"));
        var original = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_revision");

        (await RegisterAsync(world, Commit2, Test("a.spec.ts", IdA, "expect(a).toBe(2);"))).Revisions.ShouldBe(1);
        (await RevisionCount(world)).ShouldBe(2);

        var back = await RegisterAsync(world, Commit3, Test("a.spec.ts", IdA, "expect(a).toBe(1);"));
        back.Revisions.ShouldBe(0);
        (await RevisionCount(world)).ShouldBe(2);
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_revision where id = '{original}'")).ShouldBe(original);
    }

    [Fact]
    public async Task AnAssertionOnlyChange_ProducesANewAssertionHash_AndANewRevision()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA, "expect(a).toBe(1);"));
        await RegisterAsync(world, Commit2, Test("a.spec.ts", IdA, "expect(a).toBe(1); expect(b).toBe(2);"));

        (await _fixture.ScalarAsync<long>(world.Database, "select count(distinct assertion_hash) from public.test_revision")).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(distinct revision_hash) from public.test_revision")).ShouldBe(2);
    }

    [Fact]
    public async Task AFileOfAnUnknownFramework_StillGetsARevision_FlaggedWithoutAssertions()
    {
        await using var world = await StartAsync();

        await RegisterAsync(world, Commit1, new SourceFile("a_spec.rb", $"# evidgate.id: {IdA}\nit 'x' do end", "rspec", "ruby"));

        (await _fixture.ScalarAsync<string>(world.Database, "select (metadata->>'assertions_extracted') || (metadata->>'assertion_count') from public.test_revision")).ShouldBe("false0");
    }

    // ------------------------------------------------------------------ AC2 immutability

    [Fact]
    public async Task Revisions_CannotBeUpdatedOrDeleted_ByAnyRole_EvenWhenARunWouldReferenceThem()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA));

        foreach (var statement in new List<string>
        {
            "update public.test_revision set commit_sha = 'deadbee'",
            "update public.test_revision set metadata = '{}'",
            "delete from public.test_revision",
        })
        {
            await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("org", world.Org.ToString());
                await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            await using var command = new NpgsqlCommand(statement, connection, transaction);
            var app = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            app.SqlState.ShouldBe("42501", statement);

            var owner = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, statement));
            owner.MessageText.ShouldContain("revision_immutable", customMessage: statement);
        }
    }

    [Fact]
    public async Task Database_RejectsMalformedHashes_AndAnotherOrgsCase()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");
        var hash = "sha256:" + new string('b', 64);

        string Insert(string revisionHash, Guid org) =>
            $"insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash) values ('{Guid.CreateVersion7()}', '{org}', '{caseId}', '{revisionHash}', 'abcdef0', 'a', 'pw', '{hash}', '{hash}')";

        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, Insert("sha256:XYZ", world.Org))))
            .ConstraintName.ShouldBe("test_revision_hash_check");
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, Insert(hash, Guid.CreateVersion7()))))
            .ConstraintName.ShouldBe("test_revision_case_fk");
        await _fixture.ExecuteAsync(world.Database, Insert(hash, world.Org));
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, Insert(hash, world.Org))))
            .ConstraintName.ShouldBe("test_revision_case_hash_key");
    }

    // ------------------------------------------------------------------ store contract (AC1)

    [Fact]
    public async Task Store_AppendsOnce_ThenReturnsTheSameRow_AndANewHashIsANewRow()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var store = services.ServiceProvider.GetRequiredService<ITestRevisionStore>();
        var content = new TestRevisionContent(Commit2, "x/new.spec.ts", "playwright", "typescript", "test('n', () => { expect(z).toBe(9); });");

        var first = await store.AppendAsync(caseId, content, TestContext.Current.CancellationToken);
        var again = await store.AppendAsync(caseId, content with { CommitSha = Commit3 }, TestContext.Current.CancellationToken);
        var other = await store.AppendAsync(caseId, content with { Source = "different" }, TestContext.Current.CancellationToken);

        first.Created.ShouldBeTrue();
        again.Created.ShouldBeFalse();
        again.RevisionId.ShouldBe(first.RevisionId);
        again.RevisionHash.ShouldBe(first.RevisionHash);
        other.Created.ShouldBeTrue();
        other.RevisionHash.ShouldNotBe(first.RevisionHash);

        await Should.ThrowAsync<PostgresException>(async () =>
            await store.AppendAsync(Guid.CreateVersion7(), content, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ AC5 route

    [Fact]
    public async Task Route_ListsRevisionsNewestFirst_WithACursor()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA, "expect(a).toBe(1);"));
        await RegisterAsync(world, Commit2, Test("a.spec.ts", IdA, "expect(a).toBe(2);"));
        await RegisterAsync(world, Commit3, Test("a.spec.ts", IdA, "expect(a).toBe(3);"));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var page1 = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions?limit=2", world.Admin);
        page1.Status.ShouldBe(HttpStatusCode.OK);
        var items = page1.Body.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("commit_sha").GetString()).ShouldBe([Commit3, Commit2]);
        var first = items[0];
        first.GetProperty("file_path").GetString().ShouldBe("a.spec.ts");
        first.GetProperty("revision_hash").GetString().ShouldStartWith("sha256:");
        first.GetProperty("assertion_hash").GetString().ShouldStartWith("sha256:");
        first.GetProperty("case_id").GetGuid().ShouldBe(caseId);

        var cursor = page1.Body.GetProperty("next_cursor").GetString()!;
        var page2 = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions?limit=2&cursor={Uri.EscapeDataString(cursor)}", world.Admin);
        page2.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("commit_sha").GetString()).ShouldBe([Commit1]);
        page2.Body.GetProperty("next_cursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions?cursor=junk", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Route_AnotherOrgsCase_IsNotFound_AndNeedsCaseRead()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA));
        var mine = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        var rival = new World(world.Api, world.Client, world.Database, otherUser, otherOrg, otherProject);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{mine}/revisions", rival.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{mine}/revisions", TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org)))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Route_IsTwoStatements_AndRlsHidesOtherOrgsRevisions()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        world.Api.Statements.Clear();
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}/revisions", world.Admin)).Status.ShouldBe(HttpStatusCode.OK);
        world.Api.Statements.Snapshot().Count(statement => statement.Contains("from public.test_revision", StringComparison.Ordinal)).ShouldBe(1);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        await using (await scopes.BeginAsync(otherOrg, null, true, TestContext.Current.CancellationToken))
        {
            (await db.QueryAsync<Guid>("select id from public.test_revision", null, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Sync_OfTwoThousandCases_AppendsInOneStatement()
    {
        await using var world = await StartAsync();
        var tree = Enumerable.Range(0, 2000).Select(i => Test($"t/{i}.spec.ts", $"018f2f0e-8a52-7c4b-9d3e-{i:D12}", $"expect(a).toBe({i});")).ToArray();

        world.Api.Statements.Clear();
        var report = await RegisterAsync(world, Commit4, tree);

        report.Revisions.ShouldBe(2000);
        (await RevisionCount(world)).ShouldBe(2000);
        world.Api.Statements.Snapshot().Count(statement => statement.Contains("insert into public.test_revision", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Revisions_FlagFilesWhoseAssertionsCouldNotBeTrusted()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA), new SourceFile("b_spec.rb", $"# evidgate.id: {IdB}\nit 'x' do end", "rspec", "ruby"));

        var caseA = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_case where annotation_id = '{IdA}'");
        var caseB = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_case where annotation_id = '{IdB}'");

        var a = (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseA}/revisions", world.Admin)).Body.GetProperty("items")[0];
        var b = (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseB}/revisions", world.Admin)).Body.GetProperty("items")[0];

        (a.GetProperty("assertions_extracted").GetBoolean(), a.GetProperty("assertions_reliable").GetBoolean()).ShouldBe((true, true));
        (b.GetProperty("assertions_extracted").GetBoolean(), b.GetProperty("assertions_reliable").GetBoolean()).ShouldBe((false, false));
    }

    [Fact]
    public async Task Store_RefusesMalformedContent_BeforeTouchingTheDatabase()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, "select id from public.test_case");

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var store = services.ServiceProvider.GetRequiredService<ITestRevisionStore>();
        var good = new TestRevisionContent(Commit2, "a.spec.ts", "playwright", "typescript", "x");

        foreach (var bad in new List<TestRevisionContent>
        {
            good with { CommitSha = "NOT-HEX" },
            good with { FilePath = "../escape.ts" },
            good with { FilePath = "a\u202E.ts" },
            good with { Framework = "bad\0name" },
            good with { Language = new string('x', 65) },
            good with { Source = "lone" + (char)0xD800 },
        })
        {
            await Should.ThrowAsync<ArgumentException>(async () => await store.AppendAsync(caseId, bad, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Sync_ANewerSyncsRevisionsAreNeverOrderedBeforeAnOlderOnes()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Test("a.spec.ts", IdA, "expect(a).toBe(1);"));
        await RegisterAsync(world, Commit2, Test("a.spec.ts", IdA, "expect(a).toBe(2);"));

        // Written with clock_timestamp(), after the project lock: the later sync's row has the later timestamp.
        (await _fixture.ScalarAsync<string>(world.Database, "select commit_sha from public.test_revision order by created_at desc, id desc limit 1")).ShouldBe(Commit2);
    }

    [Fact]
    public async Task Sync_AFileWithBrokenText_IsAWarning_AndTheOthersStillSync()
    {
        await using var world = await StartAsync();

        var report = await RegisterAsync(
            world,
            Commit1,
            Test("good.spec.ts", IdA),
            new SourceFile("bad.spec.ts", $"// evidgate.id: {IdB}\n" + (char)0xD800, "playwright", "typescript"));

        (report.Registered, report.Warnings, report.Revisions).ShouldBe((1, 1, 1));
    }
}

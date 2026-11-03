using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 §8's statement budget for the version read model (P3): a page of the timeline is a <b>fixed</b> number of
/// statements whatever its size, and the detail route is one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared partial class.</b> The restore routes' own budget proof lives in a second file of this class (13.1.1-T3
/// slice B, <c>AuthoringCommandCounterTests.Restore*.cs</c>), so this file declares the class, the fixture and the
/// counter helpers once and names its methods <c>Queries*</c>: a partial type may not declare the same member twice.
/// </para>
/// <para>
/// <b>How statements are counted.</b> The host's <see cref="StatementLog"/> captures every command Dapper executes
/// (<c>Executing command: …</c>), transaction control excluded; the count is taken over the statements that touch this
/// module's two tables, so a permission read or a tenant <c>set_config</c> cannot decide the assertion. The routes are
/// driven through their HTTP edge, exactly as a client drives them.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class AuthoringCommandCounterTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringCommandCounterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The shared fixture, under the name the restore partial of this class reads it by
    /// (<c>AuthoringCommandCounterTests.Restore.cs</c>). One public constructor serves both files, which is what xUnit
    /// requires of a partial test class.
    /// </summary>
    private PostgresTenancyFixture Fixture => _fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string Versions => $"/v1/projects/{Project}/authoring/versions";

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

    /// <summary>
    /// The two read routes of the timeline, and only those: one statement for a page (plus the project-liveness probe the
    /// sibling read routes also issue) and one for the detail, whatever the page size and whatever the filters.
    /// </summary>
    [Fact]
    public async Task QueriesAndCommands_StayWithinBudget()
    {
        await using var world = await StartAsync();

        var entity = Guid.CreateVersion7();

        // Three rows so a page can be short, full and empty without changing the statement count.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.authoring_version
              (id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content, content_hash,
               source, author_membership_id, reason, ai_run_id, created_at)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', 'feature_node', '{entity:D}', g,
                   gen_random_uuid(), 'create', jsonb_build_object('title', 'seed'),
                   'sha256:2222222222222222222222222222222222222222222222222222222222222222', 'manual', null, null, null,
                   timestamptz '2026-10-01 00:00:00+00' + g * interval '1 second'
              from generate_series(1, 3) g
            """);

        var version = await _fixture.ScalarAsync<Guid>(
            world.Database, $"select id from public.authoring_version where project_id = '{world.Project:D}' limit 1");

        async Task<(int Page, int Probe, int DetailPage, int DetailProbe)> CountAsync()
        {
            world.Api.Statements.Clear();
            (await world.Client.SendAsync(HttpMethod.Get, world.Versions, world.As)).Status.ShouldBe(HttpStatusCode.OK);
            var first = Statements(world);

            world.Api.Statements.Clear();
            (await world.Client.SendAsync(HttpMethod.Get, $"{world.Versions}?limit=200", world.As)).Status.ShouldBe(HttpStatusCode.OK);
            var second = Statements(world);

            world.Api.Statements.Clear();
            (await world.Client.SendAsync(HttpMethod.Get, $"{world.Versions}/{version:D}", world.As)).Status.ShouldBe(HttpStatusCode.OK);
            var detail = Statements(world);

            second.Page.ShouldBe(first.Page, "the page is one statement whatever its size");
            second.Probe.ShouldBe(first.Probe);

            return (first.Page, first.Probe, detail.Page, detail.Probe);
        }

        var counted = await CountAsync();

        counted.Page.ShouldBe(1, "a timeline page is exactly one statement, whatever its size");
        counted.DetailPage.ShouldBe(
            1,
            "route 23 answers the row and its project liveness in one statement, so no probe of its own");

        // The probe is the module's own project-liveness read; tenant resolution may read `public.project` too, which is
        // why the assertion is a bound and not a single value.
        counted.Probe.ShouldBeGreaterThanOrEqualTo(1, "the handler's own project-liveness probe");
        counted.Probe.ShouldBeLessThanOrEqualTo(2, "and at most one more from the request's project resolution");
        counted.DetailProbe.ShouldBeLessThanOrEqualTo(1, "the detail statement's exists is inside its own statement");
    }

    /// <summary>The statements of one request that touch this module's tables: the version rows and the project probe.</summary>
    private static (int Page, int Probe) Statements(World world)
    {
        var snapshot = world.Api.Statements.Snapshot();

        var page = snapshot.Count(statement => statement.Contains("from public.authoring_version", StringComparison.Ordinal));

        // The detail statement carries `public.project` inside its own `exists`, so a probe is a statement that mentions
        // the project without mentioning a version.
        var probe = snapshot.Count(statement =>
            statement.Contains("from public.project", StringComparison.Ordinal)
            && !statement.Contains("public.authoring_version", StringComparison.Ordinal));

        return (page, probe);
    }
}

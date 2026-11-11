using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The per-project retention settings through the real host, the real C2 pipeline, the real evaluator and a migrated
/// PostgreSQL: the platform defaults a project without a row is served, the round trip of a stored window, the
/// configured floor and ceiling (the deployment configuration is what the refusals are judged against), the strict
/// body, the permission gate, the service-token ceiling, the cross-tenant answer and two concurrent writes. Every
/// caller is a persisted member of the org, because <c>project:read</c>/<c>project:write</c> are member permissions
/// outside <c>ServiceRoleCeiling</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RetentionPolicyHttpTests : IClassFixture<TenancyHostPool>
{
    /// <summary>The founder role of the test org: it holds <c>project:read</c> and <c>project:write</c>.</summary>
    private const string WriterRole = "tenant_admin";

    /// <summary>The built-in role that holds <c>project:read</c> and not <c>project:write</c>.</summary>
    private const string ReaderRole = "viewer";

    /// <summary>The deployment range the bound tests configure, narrower than the shipped defaults on purpose.</summary>
    private static readonly IReadOnlyDictionary<string, string?> ConfiguredLimits = new Dictionary<string, string?>
    {
        ["EVIDGATE:Retention:MinArtifactDays"] = "2",
        ["EVIDGATE:Retention:MaxArtifactDays"] = "30",
        ["EVIDGATE:Retention:MinMetadataDays"] = "5",
        ["EVIDGATE:Retention:MaxMetadataDays"] = "60",
        ["EVIDGATE:Retention:DefaultArtifactDays"] = "10",
        ["EVIDGATE:Retention:DefaultMetadataDays"] = "20",
    };

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public RetentionPolicyHttpTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    /// <summary>
    /// AC1: a project with no row is served the configured platform defaults, and the read creates no row — the
    /// defaults are answered from configuration, never persisted.
    /// </summary>
    [Fact]
    public async Task GetRetentionPolicy_NoRow_ServesConfiguredDefaultsWithoutWriting()
    {
        await using var world = await StartAsync();

        var answer = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), world.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("artifact_days").GetInt32().ShouldBe(10);
        answer.Body.GetProperty("metadata_days").GetInt32().ShouldBe(20);
        (await RowCountAsync(world)).ShouldBe(0, "a read must never create the policy row");
    }

    /// <summary>AC2 (positive control): a window inside the configured range is stored and read back unchanged.</summary>
    [Fact]
    public async Task PutRetentionPolicy_WithinRange_StoresAndReadsBack()
    {
        await using var world = await StartAsync();

        var written = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), world.Caller, new { artifact_days = 30, metadata_days = 60 });

        written.Status.ShouldBe(HttpStatusCode.OK);
        written.Body.GetProperty("artifact_days").GetInt32().ShouldBe(30);
        written.Body.GetProperty("metadata_days").GetInt32().ShouldBe(60);

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), world.Caller);

        read.Status.ShouldBe(HttpStatusCode.OK);
        read.Body.GetProperty("artifact_days").GetInt32().ShouldBe(30);
        read.Body.GetProperty("metadata_days").GetInt32().ShouldBe(60);
        (await RowCountAsync(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.retention_policy where org_id = '{world.Org}' and project_id = '{world.Project}' and version >= 1"))
            .ShouldBe(1);
    }

    /// <summary>
    /// AC2: each configured bound is enforced with <c>retention_out_of_range</c> naming the member and the bound it
    /// missed, and a refused write stores nothing. The expectations name the configured numbers, so a range that
    /// stopped reading configuration (or a floor that moved) fails this test.
    /// </summary>
    [Theory]
    [InlineData(1, 20, "artifact_days", "below_min_artifact_days", "retention_limits.min_artifact_days", "2")]
    [InlineData(31, 40, "artifact_days", "above_max_artifact_days", "retention_limits.max_artifact_days", "30")]
    [InlineData(10, 4, "metadata_days", "below_min_metadata_days", "retention_limits.min_metadata_days", "5")]
    [InlineData(10, 61, "metadata_days", "above_max_metadata_days", "retention_limits.max_metadata_days", "60")]
    [InlineData(20, 15, "metadata_days", "below_artifact_days", "retention_limits.min_metadata_days", "5")]
    public async Task PutRetentionPolicy_OutsideConfiguredRange_Is422AndWritesNothing(
        int artifactDays,
        int metadataDays,
        string field,
        string reason,
        string boundName,
        string bound)
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), world.Caller, new { artifact_days = artifactDays, metadata_days = metadataDays });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("retention_out_of_range");
        var details = Details(refused);
        details.ShouldContain(detail =>
            detail.GetProperty("path").GetString() == field
            && detail.GetProperty("reason").GetString() == reason);
        details.ShouldContain(detail =>
            detail.GetProperty("path").GetString() == boundName
            && detail.GetProperty("reason").GetString() == bound);
        (await RowCountAsync(world)).ShouldBe(0, "a refused write must store nothing");
    }

    /// <summary>The closed body: a member the route does not accept is refused by name and stores nothing.</summary>
    [Fact]
    public async Task PutRetentionPolicy_UnknownMember_Is422AndWritesNothing()
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendAsync(
            HttpMethod.Put,
            Url(world.Project),
            world.Caller,
            new { artifact_days = 10, metadata_days = 20, retention_days = 5 });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "/retention_days"
            && detail.GetProperty("reason").GetString() == "additional_properties");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A member name that is not valid UTF-16 is a caller error answered under the stable placeholder path: reading
    /// the name for a detail would throw, and writing it into the envelope would throw again. The first body carries
    /// no valid member at all, so it also pins the order the checks run in: the name scan answers before the
    /// missing-member check can name <c>artifact_days</c>.
    /// </summary>
    [Theory]
    [InlineData("""{"\ud800":1}""")]
    [InlineData("""{"artifact_days":10,"metadata_days":20,"\ud800":1}""")]
    public async Task PutRetentionPolicy_LoneSurrogateMemberName_Is422UnderThePlaceholderPath(string json)
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        var detail = Details(refused).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/<invalid-member-name>");
        detail.GetProperty("reason").GetString().ShouldBe("additional_properties");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A member name longer than the echo cap answers the same placeholder rather than a prefix that could end
    /// inside the surrogate pair of a character straddling the boundary.
    /// </summary>
    [Fact]
    public async Task PutRetentionPolicy_UnknownMemberLongerThanTheEchoCap_Is422UnderThePlaceholderPath()
    {
        await using var world = await StartAsync();
        var name = new string('a', 63) + "\U0001F642" + "b";
        name.Length.ShouldBeGreaterThan(64, "the name must straddle the echo cap with a surrogate pair");

        var refused = await world.Client.SendAsync(
            HttpMethod.Put,
            Url(world.Project),
            world.Caller,
            new Dictionary<string, int> { ["artifact_days"] = 10, ["metadata_days"] = 20, [name] = 1 });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        var detail = Details(refused).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/<invalid-member-name>");
        detail.GetProperty("reason").GetString().ShouldBe("additional_properties");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A member given twice is refused like a malformed body: the reader would otherwise keep the last value and the
    /// request would store a window the caller may not have meant.
    /// </summary>
    [Fact]
    public async Task PutRetentionPolicy_RepeatedMember_Is422AndWritesNothing()
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendRawAsync(
            HttpMethod.Put,
            Url(world.Project),
            world.Caller,
            """{"artifact_days":10,"artifact_days":20,"metadata_days":20}""");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(detail => detail.GetProperty("reason").GetString() == "invalid_json");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>A missing member is its own reason, never silently read as zero.</summary>
    [Fact]
    public async Task PutRetentionPolicy_MissingMember_Is422AndWritesNothing()
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), world.Caller, new { artifact_days = 10 });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "metadata_days"
            && detail.GetProperty("reason").GetString() == "required");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// AC3: the write needs <c>project:write</c>. The same member and the same project read successfully, so the
    /// refusal is the missing permission and nothing else.
    /// </summary>
    [Fact]
    public async Task PutRetentionPolicy_WithoutProjectWrite_Is403()
    {
        await using var world = await StartAsync(ReaderRole);

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), world.Caller);
        read.Status.ShouldBe(HttpStatusCode.OK);

        var refused = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), world.Caller, new { artifact_days = 10, metadata_days = 20 });

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// The routes are member routes: <c>project:read</c>/<c>project:write</c> sit outside <c>ServiceRoleCeiling</c>,
    /// so a service token claiming them is refused on both verbs and nothing is written.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_ServiceTokenClaimingTheProjectPermissions_Is403OnBothVerbs()
    {
        await using var world = await StartAsync();

        var service = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: [Permissions.Project.Read, Permissions.Project.Write]);

        (await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), service))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), service, new { artifact_days = 10, metadata_days = 20 }))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// AC5: another tenant's project id is 404 on both verbs — never 403 and never that tenant's policy, because row
    /// level security hides the project row entirely.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_CrossTenantProject_Is404OnBothVerbs()
    {
        await using var world = await StartAsync();

        using var setup = world.Setup.CreateClient();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(setup, otherUser, Unique.Slug("other"));
        var otherProject = await CreateProjectAsync(setup, otherUser, otherOrg, Unique.Slug("other-project"));

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(otherProject), world.Caller);
        read.Status.ShouldBe(HttpStatusCode.NotFound);

        var write = await world.Client.SendAsync(
            HttpMethod.Put, Url(otherProject), world.Caller, new { artifact_days = 10, metadata_days = 20 });
        write.Status.ShouldBe(HttpStatusCode.NotFound);

        (await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.retention_policy where org_id = '{otherOrg}'"))
            .ShouldBe(0, "the foreign project's policy must not be written");
    }

    /// <summary>An unknown project id is the same 404, so the answer never distinguishes unknown from foreign.</summary>
    [Fact]
    public async Task RetentionPolicy_UnknownProject_Is404OnBothVerbs()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        (await world.Client.SendAsync(HttpMethod.Get, Url(unknown), world.Caller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(
            HttpMethod.Put, Url(unknown), world.Caller, new { artifact_days = 10, metadata_days = 20 }))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// AC4: two concurrent writes converge on the one row the upsert key allows — no duplicate-key answer, and the
    /// committed values are one of the two requests.
    /// </summary>
    [Fact]
    public async Task PutRetentionPolicy_ConcurrentWrites_ConvergeOnOneRow()
    {
        await using var world = await StartAsync();

        var first = world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), world.Caller, new { artifact_days = 5, metadata_days = 20 });
        var second = world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), world.Caller, new { artifact_days = 6, metadata_days = 21 });

        var answers = await Task.WhenAll(first, second);

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        (await RowCountAsync(world)).ShouldBe(1);

        var stored = await _fixture.ScalarAsync<int>(
            world.Database,
            $"select artifact_days from public.retention_policy where org_id = '{world.Org}' and project_id = '{world.Project}'");
        stored.ShouldBeOneOf(5, 6);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.retention_policy where org_id = '{world.Org}' and project_id = '{world.Project}'"))
            .ShouldBeGreaterThanOrEqualTo(2, "the second committed write is an update, so the trigger bumped the version");
    }

    /// <summary>The 00 §9.2 <c>details</c> rows of a refused answer.</summary>
    private static JsonElement[] Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    private static string Url(Guid project) => $"/v1/projects/{project:D}/retention";

    /// <summary>The policy rows this test's project owns (never an unfiltered count: the database is shared).</summary>
    private Task<long> RowCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.retention_policy where org_id = '{world.Org}' and project_id = '{world.Project}'");

    /// <summary>
    /// A migrated database with this test's own org and project (created through the real registry routes), a host
    /// with the real evaluator, resolver and pipeline plus the deployment range <see cref="ConfiguredLimits"/>, and
    /// the caller: the founder (<c>tenant_admin</c>) unless <paramref name="roleCode"/> asks for another built-in
    /// role, which the founder grants through the real membership route.
    /// </summary>
    private async Task<World> StartAsync(string roleCode = WriterRole)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var setup = _hosts.RealResolver(database);
        var founder = Guid.CreateVersion7();
        Guid org;
        Guid project;
        var caller = founder;

        using (var client = setup.CreateClient())
        {
            org = await CreateOrgAsync(client, founder, Unique.Slug("retention"));
            project = await CreateProjectAsync(client, founder, org, Unique.Slug("retention-project"));

            if (roleCode != WriterRole)
            {
                caller = await AddMemberAsync(database, client, org, founder, roleCode);
            }
        }

        var host = _hosts.RealResolver(database, ConfiguredLimits);

        return new World(
            setup,
            host,
            host.CreateClient(),
            TenancyApiFactory.Resolved(user: caller, orgHeader: org),
            database,
            org,
            project);
    }

    /// <summary>
    /// A second active member of the org holding one built-in role: the account and membership rows are seeded, the
    /// role is granted through the real route so the evaluator sees the same rows production would.
    /// </summary>
    private async Task<Guid> AddMemberAsync(
        string database,
        HttpClient client,
        Guid org,
        Guid founder,
        string roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        var email = $"member-{user:N}@example.test";

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{org}', '{user}', '{email}', 'active')
            """);

        var role = await _fixture.ScalarAsync<Guid>(
            database, $"select id from public.role where code = '{roleCode}' and org_id is null");
        var granted = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/memberships/{membership}/roles",
            TenancyApiFactory.Resolved(user: founder, orgHeader: org),
            new { role_id = role },
            NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return user;
    }

    /// <summary>One test's hosts, client, caller and tenant.</summary>
    private sealed record World(
        TenancyApiFactory Setup,
        TenancyApiFactory Host,
        HttpClient Client,
        Action<HttpRequestHeaders> Caller,
        string Database,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            await Setup.DisposeAsync();
        }
    }
}

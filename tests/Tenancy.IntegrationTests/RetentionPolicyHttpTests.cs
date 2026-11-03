using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The per-project retention settings through the real host, the real C2 pipeline and a migrated PostgreSQL: the
/// platform defaults a project without a row is served, the round trip of a stored window, the configured floor and
/// ceiling (the deployment configuration is what the refusals are judged against), the strict body, the permission
/// gate, the cross-tenant answer and two concurrent writes.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RetentionPolicyHttpTests
{
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

    public RetentionPolicyHttpTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC1: a project with no row is served the configured platform defaults, and the read creates no row — the
    /// defaults are answered from configuration, never persisted.
    /// </summary>
    [Fact]
    public async Task GetRetentionPolicy_NoRow_ServesConfiguredDefaultsWithoutWriting()
    {
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        var answer = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), _ => { });

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("artifact_days").GetInt32().ShouldBe(10);
        answer.Body.GetProperty("metadata_days").GetInt32().ShouldBe(20);
        (await RowCountAsync(world)).ShouldBe(0, "a read must never create the policy row");
    }

    /// <summary>AC2 (positive control): a window inside the configured range is stored and read back unchanged.</summary>
    [Fact]
    public async Task PutRetentionPolicy_WithinRange_StoresAndReadsBack()
    {
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        var written = await world.Client.SendAsync(HttpMethod.Put, Url(world.Project), _ => { }, new { artifact_days = 30, metadata_days = 60 });

        written.Status.ShouldBe(HttpStatusCode.OK);
        written.Body.GetProperty("artifact_days").GetInt32().ShouldBe(30);
        written.Body.GetProperty("metadata_days").GetInt32().ShouldBe(60);

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), _ => { });

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
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        var refused = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), _ => { }, new { artifact_days = artifactDays, metadata_days = metadataDays });

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
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        var refused = await world.Client.SendAsync(
            HttpMethod.Put,
            Url(world.Project),
            _ => { },
            new { artifact_days = 10, metadata_days = 20, retention_days = 5 });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "/retention_days"
            && detail.GetProperty("reason").GetString() == "additional_properties");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>A missing member is its own reason, never silently read as zero.</summary>
    [Fact]
    public async Task PutRetentionPolicy_MissingMember_Is422AndWritesNothing()
    {
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        var refused = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), _ => { }, new { artifact_days = 10 });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Details(refused).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "metadata_days"
            && detail.GetProperty("reason").GetString() == "required");
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// AC3: the write needs <c>project:write</c>. The same principal and the same project read successfully, so the
    /// refusal is the missing permission and nothing else.
    /// </summary>
    [Fact]
    public async Task PutRetentionPolicy_WithoutProjectWrite_Is403()
    {
        await using var world = await StartAsync([Permissions.Project.Read]);

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), _ => { });
        read.Status.ShouldBe(HttpStatusCode.OK);

        var refused = await world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), _ => { }, new { artifact_days = 10, metadata_days = 20 });

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await RowCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// AC5: another tenant's project id is 404 on both verbs — never 403 and never that tenant's policy, because row
    /// level security hides the project row entirely.
    /// </summary>
    [Fact]
    public async Task RetentionPolicy_CrossTenantProject_Is404OnBothVerbs()
    {
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        using var setup = world.Setup.CreateClient();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(setup, otherUser, Unique.Slug("other"));
        var otherProject = await CreateProjectAsync(setup, otherUser, otherOrg, Unique.Slug("other-project"));

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(otherProject), _ => { });
        read.Status.ShouldBe(HttpStatusCode.NotFound);

        var write = await world.Client.SendAsync(
            HttpMethod.Put, Url(otherProject), _ => { }, new { artifact_days = 10, metadata_days = 20 });
        write.Status.ShouldBe(HttpStatusCode.NotFound);

        (await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.retention_policy where org_id = '{otherOrg}'"))
            .ShouldBe(0, "the foreign project's policy must not be written");
    }

    /// <summary>An unknown project id is the same 404, so the answer never distinguishes unknown from foreign.</summary>
    [Fact]
    public async Task RetentionPolicy_UnknownProject_Is404OnBothVerbs()
    {
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);
        var unknown = Guid.CreateVersion7();

        (await world.Client.SendAsync(HttpMethod.Get, Url(unknown), _ => { })).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(
            HttpMethod.Put, Url(unknown), _ => { }, new { artifact_days = 10, metadata_days = 20 }))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// AC4: two concurrent writes converge on the one row the upsert key allows — no duplicate-key answer, and the
    /// committed values are one of the two requests.
    /// </summary>
    [Fact]
    public async Task PutRetentionPolicy_ConcurrentWrites_ConvergeOnOneRow()
    {
        await using var world = await StartAsync([Permissions.Project.Read, Permissions.Project.Write]);

        var first = world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), _ => { }, new { artifact_days = 5, metadata_days = 20 });
        var second = world.Client.SendAsync(
            HttpMethod.Put, Url(world.Project), _ => { }, new { artifact_days = 6, metadata_days = 21 });

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
    /// publishing <paramref name="permissions"/> for that project, and the deployment range
    /// <see cref="ConfiguredLimits"/>.
    /// </summary>
    private async Task<World> StartAsync(IReadOnlyCollection<string> permissions)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var setup = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var client = setup.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(client, user, Unique.Slug("retention"));
            project = await CreateProjectAsync(client, user, org, Unique.Slug("retention-project"));
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(project, permissions),
            ConfiguredLimits);

        return new World(setup, host, host.CreateClient(), database, org, project);
    }

    /// <summary>One test's hosts, client and tenant.</summary>
    private sealed record World(
        TenancyApiFactory Setup,
        TenancyApiFactory Host,
        HttpClient Client,
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

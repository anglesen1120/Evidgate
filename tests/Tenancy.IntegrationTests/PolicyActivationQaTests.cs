using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA exploratory and negative tests for <c>POST /v1/projects/{p}/policy-activation</c> (07.1.1-T4): body-shape
/// boundaries, the unstorable-text guard and the unknown-member detail cap, the 4 KiB body size cap, a soft-deleted
/// project, a cross-org caller, refusals writing no audit row, the retired-to-active refusal, one audit row per
/// successful re-activation, and the app role's write grant on <c>run</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class PolicyActivationQaTests
{
    private const string BodyA = """{"flaky_budget":3,"required_suites":["smoke","regression"]}""";

    private const string BodyB = """{"flaky_budget":4,"required_suites":["smoke","regression"]}""";

    private const string BodyC = """{"flaky_budget":5,"required_suites":["smoke","regression"]}""";

    private readonly PostgresTenancyFixture _fixture;

    public PolicyActivationQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

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

    private static async Task<Guid> CreateRevisionAsync(World world, string body, Guid? project = null)
    {
        var created = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policies",
            world.Admin,
            body);
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    private static Task<ApiAnswer> ActivateRawAsync(World world, string raw, Action<HttpRequestHeaders>? caller = null, Guid? project = null) =>
        SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{project ?? world.Project}/policy-activation",
            caller ?? world.Admin,
            raw,
            NewKey());

    private static Task<ApiAnswer> ActivateAsync(World world, Guid revisionId, Action<HttpRequestHeaders>? caller = null, Guid? project = null) =>
        ActivateRawAsync(world, $$"""{"policy_revision_id":"{{revisionId}}"}""", caller, project);

    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private async Task<string> StateAsync(World world, Guid revisionId) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.policy_revision where id = '{revisionId}'");

    private async Task<long> ActiveCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.policy_revision where state = 'active'");

    private async Task<long> AuditCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.audit_log where action = 'policy.activate'");

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"policy_revision_id":"not-a-guid"}""")]
    [InlineData("""{"policy_revision_id":"REV","extra":1}""")]
    [InlineData("""{"policy_revision_id":"REV","mode":"Advisory"}""")]
    [InlineData("""{"policy_revision_id":"REV","mode":"ENFORCING"}""")]
    [InlineData("""{"policy_revision_id":"REV","mode":"enforcing"}""")]
    [InlineData("""{"policy_revision_id":null}""")]
    [InlineData("""{"policy_revision_id":12345}""")]
    public async Task Activation_InvalidBodyShape_IsRefused_WithoutWriting(string template)
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var raw = template.Replace("REV", revision.ToString());

        var answer = await ActivateRawAsync(world, raw);

        var status = (int)answer.Status;
        (status >= 400 && status < 500).ShouldBeTrue($"expected a 4xx for body {raw}, got {status}");
        (await ActiveCountAsync(world)).ShouldBe(0L);
        (await AuditCountAsync(world)).ShouldBe(0L);
        (await StateAsync(world, revision)).ShouldBe("draft");
    }

    [Fact]
    public async Task Activation_DuplicateMemberNames_IsNotAServerError_AndWritesOnlyOnSuccess()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var raw = $$"""{"policy_revision_id":"{{revision}}","policy_revision_id":"00000000-0000-0000-0000-000000000000"}""";

        var answer = await ActivateRawAsync(world, raw);

        var status = (int)answer.Status;
        (status >= 500 && status < 600).ShouldBeFalse($"server error {status} for a duplicate-key body");
        if (answer.Status == HttpStatusCode.OK)
        {
            (await StateAsync(world, revision)).ShouldBe("active");
            (await AuditCountAsync(world)).ShouldBe(1L);
        }
        else
        {
            (await ActiveCountAsync(world)).ShouldBe(0L);
            (await AuditCountAsync(world)).ShouldBe(0L);
        }
    }

    [Theory]
    [InlineData("""{"policy_revision_id":"\ud800"}""")]
    [InlineData("""{"\ud800":1}""")]
    public async Task Activation_LoneSurrogate_Is422_AndWritesNothing(string raw)
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        var answer = await ActivateRawAsync(world, raw);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ActiveCountAsync(world)).ShouldBe(0L);
        (await AuditCountAsync(world)).ShouldBe(0L);
        (await StateAsync(world, revision)).ShouldBe("draft");
    }

    [Fact]
    public async Task Activation_ManyUnknownMembers_Are422_WithAtMostTwentyDetails()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var members = string.Join(',', Enumerable.Range(0, 400).Select(i => $"\"m{i}\":1"));
        var raw = $$"""{"policy_revision_id":"{{revision}}",{{members}}}""";
        Encoding.UTF8.GetByteCount(raw).ShouldBeLessThanOrEqualTo(4 * 1024);

        var answer = await ActivateRawAsync(world, raw);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.Body.GetProperty("error").GetProperty("details").GetArrayLength().ShouldBe(20);
        (await ActiveCountAsync(world)).ShouldBe(0L);
        (await AuditCountAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Activation_LongUnknownMemberName_Is422_WithThePathCappedAtTwoHundred()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var raw = $$"""{"policy_revision_id":"{{revision}}","{{new string('n', 300)}}":1}""";

        var answer = await ActivateRawAsync(world, raw);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var details = answer.Body.GetProperty("error").GetProperty("details");
        details.GetArrayLength().ShouldBe(1);
        details[0].GetProperty("path").GetString()!.Length.ShouldBe(200);
        details[0].GetProperty("reason").GetString().ShouldBe("unknown_member");
    }

    [Fact]
    public async Task Activation_BodyOverFourKibibytes_Is413_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var pad = new string('x', 4 * 1024);
        var raw = $$"""{"policy_revision_id":"{{revision}}","pad":"{{pad}}"}""";
        Encoding.UTF8.GetByteCount(raw).ShouldBeGreaterThan(4 * 1024);

        var answer = await ActivateRawAsync(world, raw);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ActiveCountAsync(world)).ShouldBe(0L);
        (await AuditCountAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Activation_SoftDeletedProject_Is404_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.project set status = 'deleted' where id = '{world.Project}'");

        var answer = await ActivateAsync(world, revision);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        (await ActiveCountAsync(world)).ShouldBe(0L);
        (await AuditCountAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Activation_CallerFromAnotherOrg_OnThisProject_Is404_AndLeavesPointerAlone()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        var otherOrg = await CreateOrgAsync(world.Client, world.User, "globex");
        var otherCaller = TenancyApiFactory.Resolved(user: world.User, orgHeader: otherOrg);

        var answer = await ActivateAsync(world, revision, otherCaller);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        (await ActiveCountAsync(world)).ShouldBe(0L);
        (await AuditCountAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Activation_RetiredRevision_IsRefused409_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);
        (await ActivateAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK);
        (await ActivateAsync(world, second)).Status.ShouldBe(HttpStatusCode.OK);
        var auditBefore = await AuditCountAsync(world);

        var answer = await ActivateAsync(world, first);

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        (await StateAsync(world, first)).ShouldBe("retired");
        (await StateAsync(world, second)).ShouldBe("active");
        (await ActiveCountAsync(world)).ShouldBe(1L);
        (await AuditCountAsync(world)).ShouldBe(auditBefore);
    }

    [Fact]
    public async Task Activation_ThreeRevisionChain_KeepsExactlyOneActive_AndOneAuditRowPerCall()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);
        var third = await CreateRevisionAsync(world, BodyC);

        (await ActivateAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK);
        (await ActivateAsync(world, second)).Status.ShouldBe(HttpStatusCode.OK);
        (await ActivateAsync(world, third)).Status.ShouldBe(HttpStatusCode.OK);

        (await StateAsync(world, first)).ShouldBe("retired");
        (await StateAsync(world, second)).ShouldBe("retired");
        (await StateAsync(world, third)).ShouldBe("active");
        (await ActiveCountAsync(world)).ShouldBe(1L);
        (await AuditCountAsync(world)).ShouldBe(3L);
    }

    [Fact]
    public async Task Activation_ReactivatingTheActiveRevision_Succeeds_AndWritesOneMoreAuditRow()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);
        (await ActivateAsync(world, revision)).Status.ShouldBe(HttpStatusCode.OK);
        (await AuditCountAsync(world)).ShouldBe(1L);

        var answer = await ActivateAsync(world, revision);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        (await StateAsync(world, revision)).ShouldBe("active");
        (await ActiveCountAsync(world)).ShouldBe(1L);
        (await AuditCountAsync(world)).ShouldBe(2L);
    }

    [Fact]
    public async Task AppRole_CannotUpdateRunPolicyRevisionId_AtTheDatabase()
    {
        await using var world = await StartAsync();
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "update public.run set policy_revision_id = null",
            connection);

        var error = await Should.ThrowAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        error.SqlState.ShouldBe("42501");
    }
}

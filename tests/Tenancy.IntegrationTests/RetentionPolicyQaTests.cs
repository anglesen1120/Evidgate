using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class RetentionPolicyQaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private const string ValidBody = """{"artifact_days":14,"metadata_days":90}""";

    [Fact]
    public async Task GetRetentionPolicy_RepeatedDefaultReads_Return14And90WithoutCreatingRows()
    {
        await using var world = await StartAsync();

        for (var read = 0; read < 2; read++)
        {
            var answer = await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), world.Caller);
            AssertPolicy(answer, 14, 90);
            (await RowCountAsync(world)).ShouldBe(0);
        }

        await AssertValidWriteAsync(world);
        (await RowCountAsync(world)).ShouldBe(1);
    }

    [Theory]
    [InlineData("artifact_days", "0", "retention_out_of_range", "below_min_artifact_days")]
    [InlineData("artifact_days", "-1", "retention_out_of_range", "below_min_artifact_days")]
    [InlineData("metadata_days", "0", "retention_out_of_range", "below_min_metadata_days")]
    [InlineData("metadata_days", "-1", "retention_out_of_range", "below_min_metadata_days")]
    [InlineData("artifact_days", "2147483648", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "2147483648", "request_invalid", "invalid_type")]
    [InlineData("artifact_days", "5.0", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "90.0", "request_invalid", "invalid_type")]
    [InlineData("artifact_days", "\"14\"", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "\"90\"", "request_invalid", "invalid_type")]
    [InlineData("artifact_days", "null", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "null", "request_invalid", "invalid_type")]
    [InlineData("artifact_days", "true", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "false", "request_invalid", "invalid_type")]
    [InlineData("artifact_days", "{\"value\":14}", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "{\"value\":90}", "request_invalid", "invalid_type")]
    [InlineData("artifact_days", "[14]", "request_invalid", "invalid_type")]
    [InlineData("metadata_days", "[90]", "request_invalid", "invalid_type")]
    public async Task PutRetentionPolicy_InvalidWindow_Is422AndDoesNotWrite(
        string field, string value, string code, string reason)
    {
        await using var world = await StartAsync();
        var artifact = field == "artifact_days" ? value : "14";
        var metadata = field == "metadata_days" ? value : "90";
        var json = $$"""{"artifact_days":{{artifact}},"metadata_days":{{metadata}}}""";

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        AssertError(refused, HttpStatusCode.UnprocessableEntity, code);
        refused.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(detail =>
            detail.GetProperty("path").GetString() == field && detail.GetProperty("reason").GetString() == reason);
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Theory]
    [InlineData("""{"artifact_days":0,"artifact_days":14,"metadata_days":90}""")]
    [InlineData("""{"artifact_days":14,"artifact_days":0,"metadata_days":90}""")]
    [InlineData("""{"artifact_days":14,"metadata_days":0,"metadata_days":90}""")]
    [InlineData("""{"artifact_days":14,"metadata_days":90,"metadata_days":0}""")]
    [InlineData("""{"artifact_days":0,"\u0061rtifact_days":14,"metadata_days":90}""")]
    public async Task PutRetentionPolicy_DuplicateWindowInEitherOrder_Is422AndDoesNotWrite(string json)
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        AssertError(refused, HttpStatusCode.UnprocessableEntity, "request_invalid");
        var detail = refused.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldHaveSingleItem();
        detail.GetProperty("reason").GetString().ShouldBe("invalid_json");
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[14,90]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("14")]
    public async Task PutRetentionPolicy_NonObjectRoot_Is422AndDoesNotWrite(string json)
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        AssertError(refused, HttpStatusCode.UnprocessableEntity, "request_invalid");
        var detail = refused.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("$");
        detail.GetProperty("reason").GetString().ShouldBe("invalid_type");
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("""{"artifact_days":14,"metadata_days":90,}""")]
    public async Task PutRetentionPolicy_MalformedJson_Is400AndDoesNotWrite(string json)
    {
        await using var world = await StartAsync();

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        AssertError(refused, HttpStatusCode.BadRequest, "request_invalid");
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    public async Task PutRetentionPolicy_NonJsonContentType_Is415AndDoesNotWrite(string mediaType)
    {
        await using var world = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(world.Project))
        {
            Content = new StringContent(ValidBody, Encoding.UTF8, mediaType),
        };
        world.Caller(request.Headers);

        using var refused = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var error = body.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("request_invalid");
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    public async Task PutRetentionPolicy_AtOrBelowBodyCap_AcceptsValidPaddedJson(int bytes)
    {
        await using var world = await StartAsync();
        var json = ValidBody.PadRight(bytes);
        Encoding.UTF8.GetByteCount(json).ShouldBe(bytes);

        var answer = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        AssertPolicy(answer, 14, 90);
        (await RowCountAsync(world)).ShouldBe(1);
    }

    [Theory]
    [InlineData(4097)]
    [InlineData(65536)]
    public async Task PutRetentionPolicy_OverBodyCap_Is413AndDoesNotWrite(int bytes)
    {
        await using var world = await StartAsync();
        var json = ValidBody.PadRight(bytes);
        Encoding.UTF8.GetByteCount(json).ShouldBe(bytes);

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, json);

        AssertError(refused, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Fact]
    public async Task RetentionPolicy_ForeignProjectWithStoredPolicy_Is404AndRemainsUnchanged()
    {
        await using var world = await StartAsync();
        var owner = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, owner, Unique.Slug("retention-other"));
        var project = await CreateProjectAsync(world.Client, owner, org, Unique.Slug("retention-project"));
        var caller = TenancyApiFactory.Resolved(user: owner, orgHeader: org);
        var body = new { artifact_days = 7, metadata_days = 45 };
        AssertPolicy(await world.Client.SendAsync(HttpMethod.Put, Url(project), caller, body), 7, 45);

        var read = await world.Client.SendAsync(HttpMethod.Get, Url(project), world.Caller);
        var write = await world.Client.SendRawAsync(HttpMethod.Put, Url(project), world.Caller, ValidBody);

        AssertError(read, HttpStatusCode.NotFound, "not_found");
        AssertError(write, HttpStatusCode.NotFound, "not_found");
        AssertPolicy(await world.Client.SendAsync(HttpMethod.Get, Url(project), caller), 7, 45);
        (await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.retention_policy where project_id = '{project}'")).ShouldBe(1);
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Fact]
    public async Task PutRetentionPolicy_ViewerCanReadButCannotWrite_Is403()
    {
        await using var world = await StartAsync();
        var viewer = await AddViewerAsync(world);
        var caller = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        AssertPolicy(await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), caller), 14, 90);

        var refused = await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), caller, ValidBody);

        AssertError(refused, HttpStatusCode.Forbidden, "forbidden");
        (await RowCountAsync(world)).ShouldBe(0);
        await AssertValidWriteAsync(world);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task RetentionPolicy_NoPrincipal_Is401(string verb)
    {
        await using var world = await StartAsync();
        var method = new HttpMethod(verb);
        var anonymous = TenancyApiFactory.Resolved(orgHeader: world.Org);

        var refused = verb == "PUT"
            ? await world.Client.SendRawAsync(method, Url(world.Project), anonymous, ValidBody)
            : await world.Client.SendAsync(method, Url(world.Project), anonymous);

        AssertError(refused, HttpStatusCode.Unauthorized, "unauthenticated");
        (await RowCountAsync(world)).ShouldBe(0);
        var control = verb == "PUT"
            ? await world.Client.SendRawAsync(method, Url(world.Project), world.Caller, ValidBody)
            : await world.Client.SendAsync(method, Url(world.Project), world.Caller);
        AssertPolicy(control, 14, 90);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task RetentionPolicy_ServicePrincipalClaimingProjectPermissions_Is403(string verb)
    {
        await using var world = await StartAsync();
        var method = new HttpMethod(verb);
        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project,
            servicePermissions: [Permissions.Project.Read, Permissions.Project.Write]);

        var refused = verb == "PUT"
            ? await world.Client.SendRawAsync(method, Url(world.Project), service, ValidBody)
            : await world.Client.SendAsync(method, Url(world.Project), service);

        AssertError(refused, HttpStatusCode.Forbidden, "forbidden");
        (await RowCountAsync(world)).ShouldBe(0);
        var control = verb == "PUT"
            ? await world.Client.SendRawAsync(method, Url(world.Project), world.Caller, ValidBody)
            : await world.Client.SendAsync(method, Url(world.Project), world.Caller);
        AssertPolicy(control, 14, 90);
    }

    [Fact]
    public async Task PutRetentionPolicy_IdenticalResendWithoutIdempotencyKey_PreservesIdentityAndWindows()
    {
        await using var world = await StartAsync();
        await AssertValidWriteAsync(world);
        var id = await fixture.ScalarAsync<Guid>(world.Database,
            $"select id from public.retention_policy where project_id = '{world.Project}'");

        await AssertValidWriteAsync(world);

        (await RowCountAsync(world)).ShouldBe(1);
        (await fixture.ScalarAsync<Guid>(world.Database,
            $"select id from public.retention_policy where project_id = '{world.Project}'")).ShouldBe(id);
        AssertPolicy(await world.Client.SendAsync(HttpMethod.Get, Url(world.Project), world.Caller), 14, 90);
    }

    private static string Url(Guid project) => $"/v1/projects/{project:D}/retention";

    private Task<long> RowCountAsync(World world) => fixture.ScalarAsync<long>(world.Database,
        $"select count(*) from public.retention_policy where project_id = '{world.Project}'");

    private static void AssertPolicy(ApiAnswer answer, int artifact, int metadata)
    {
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("artifact_days").GetInt32().ShouldBe(artifact);
        answer.Body.GetProperty("metadata_days").GetInt32().ShouldBe(metadata);
        answer.Body.EnumerateObject().Count().ShouldBe(2);
    }

    private static void AssertError(ApiAnswer answer, HttpStatusCode status, string code)
    {
        answer.Status.ShouldBe(status, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(code);
        answer.Body.GetProperty("error").GetProperty("retryable").GetBoolean().ShouldBeFalse();
    }

    private static async Task AssertValidWriteAsync(World world) => AssertPolicy(
        await world.Client.SendRawAsync(HttpMethod.Put, Url(world.Project), world.Caller, ValidBody), 14, 90);

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var host = hosts.RealResolver(database);
        var client = host.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, Unique.Slug("retention-boundary"));
        var project = await CreateProjectAsync(client, founder, org, Unique.Slug("retention-project"));
        return new World(host, client, TenancyApiFactory.Resolved(user: founder, orgHeader: org), database,
            founder, org, project);
    }

    private async Task<Guid> AddViewerAsync(World world)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        var email = $"viewer-{user:N}@example.test";
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', '{email}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{world.Org}', '{user}', '{email}', 'active')
            """);
        var role = await fixture.ScalarAsync<Guid>(world.Database,
            "select id from public.role where code = 'viewer' and org_id is null");
        var granted = await world.Client.SendAsync(HttpMethod.Post,
            $"/v1/orgs/{world.Org}/memberships/{membership}/roles", world.Caller, new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        return user;
    }

    private sealed record World(TenancyApiFactory Host, HttpClient Client, Action<HttpRequestHeaders> Caller,
        string Database, Guid Founder, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
        }
    }
}

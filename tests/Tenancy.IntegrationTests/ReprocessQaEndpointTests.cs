using System.Net;
using System.Net.Http.Headers;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class ReprocessQaEndpointTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Reprocess_ForeignReportUnderOwnRun_ReturnsNotFound()
    {
        await using var own = await StartAsync();
        await using var other = await StartAsync();

        var denied = await PostAsync(own, other.Report);

        denied.Status.ShouldBe(HttpStatusCode.NotFound);
        denied.ErrorCode.ShouldBe("not_found");
        (await RequestsAsync(own)).ShouldBe(0);
        (await PostAsync(own, own.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await PostAsync(other, other.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Reprocess_SameOrgReportUnderAnotherRun_ReturnsNotFound()
    {
        await using var world = await StartAsync();
        var otherRun = await RunRowSeeder.SeedRunAsync(fixture, world.Database, world.Org);

        var denied = await PostAsync(world, world.Report, run: otherRun);

        denied.Status.ShouldBe(HttpStatusCode.NotFound);
        denied.ErrorCode.ShouldBe("not_found");
        (await RequestsAsync(world)).ShouldBe(0);
        (await PostAsync(world, world.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("1.0.0")]
    [InlineData("abc")]
    [InlineData("1..2")]
    [InlineData("latest")]
    public async Task Reprocess_RegisteredButNotNewerVersion_ReturnsUnknown(string version)
    {
        await using var world = await StartAsync();
        await RegisterAsync(world.Database, world.Org, world.Parser, version);

        var denied = await PostAsync(world, world.Report, version: version);

        denied.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        denied.ErrorCode.ShouldBe("parser_version_unknown");
        (await RequestsAsync(world)).ShouldBe(0);
        (await PostAsync(world, world.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Reprocess_VersionWithNinetyNineCharacters_IsUnprocessable()
    {
        await using var world = await StartAsync();

        var denied = await PostAsync(world, world.Report, version: new string('9', 99));

        denied.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        denied.ErrorCode.ShouldBe("request_invalid");
        (await RequestsAsync(world)).ShouldBe(0);
        (await PostAsync(world, world.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("missing_report", HttpStatusCode.UnprocessableEntity)]
    [InlineData("missing_version", HttpStatusCode.UnprocessableEntity)]
    [InlineData("invalid_uuid", HttpStatusCode.BadRequest)]
    [InlineData("null_version", HttpStatusCode.UnprocessableEntity)]
    public async Task Reprocess_InvalidBody_IsRefusedAtItsValidationBoundary(string shape, HttpStatusCode status)
    {
        await using var world = await StartAsync();
        var body = new Dictionary<string, object?>
        {
            ["raw_report_id"] = world.Report,
            ["parser_version"] = "1.1.0",
        };
        switch (shape)
        {
            case "missing_report": body.Remove("raw_report_id"); break;
            case "missing_version": body.Remove("parser_version"); break;
            case "invalid_uuid": body["raw_report_id"] = "not-a-uuid"; break;
            case "null_version": body["parser_version"] = null; break;
        }

        var denied = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/runs/{world.Run}/reprocess", world.Admin, body, NewKey());

        denied.Status.ShouldBe(status);
        denied.ErrorCode.ShouldBe("request_invalid");
        (await RequestsAsync(world)).ShouldBe(0);
        (await PostAsync(world, world.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Reprocess_AdditionalPropertyAllowedBySchema_PreservesRecognizedBody()
    {
        await using var world = await StartAsync();
        var body = new { raw_report_id = world.Report, parser_version = "1.1.0", unexpected = true };

        var accepted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/runs/{world.Run}/reprocess", world.Admin, body, NewKey());

        accepted.Status.ShouldBe(HttpStatusCode.Accepted);
        var plain = await PostAsync(world, world.Report);
        plain.Status.ShouldBe(HttpStatusCode.Accepted);
        plain.Id("parsing_id").ShouldBe(accepted.Id("parsing_id"));
        (await RequestsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Reprocess_SameKeyDifferentValidReport_ReturnsConflict()
    {
        await using var world = await StartAsync();
        var secondReport = await SeedReportAsync(world, world.Run);
        var key = NewKey();
        var first = await PostAsync(world, world.Report, key: key);
        first.Status.ShouldBe(HttpStatusCode.Accepted);

        var denied = await PostAsync(world, secondReport, key: key);

        denied.Status.ShouldBe(HttpStatusCode.Conflict);
        denied.ErrorCode.ShouldBe("idempotency_conflict");
        (await RequestsAsync(world)).ShouldBe(1);
        var replay = await PostAsync(world, world.Report, key: key);
        replay.Id("parsing_id").ShouldBe(first.Id("parsing_id"));
        (await PostAsync(world, secondReport)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await RequestsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task Reprocess_ActiveMemberWithoutRunCreate_ReturnsForbidden()
    {
        await using var world = await StartAsync();
        var member = await AddMemberAsync(
            fixture, world.Client, world.Database, world.Org, world.Admin, roleCode: null);

        var denied = await PostAsync(world, world.Report,
            caller: TenancyApiFactory.Resolved(user: member, orgHeader: world.Org));

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await RequestsAsync(world)).ShouldBe(0);
        (await PostAsync(world, world.Report)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Reprocess_CanceledRun_AcceptsWithoutBillingOrStateTransition()
    {
        await using var world = await StartAsync();
        await fixture.ExecuteAsync(world.Database,
            $"update public.run set state = 'canceled' where id = '{world.Run:D}'");
        var before = await fixture.ScalarAsync<string>(world.Database,
            $"select md5(t::text) from public.run t where id = '{world.Run:D}'");

        (await PostAsync(world, world.Report)).Status.ShouldBe(HttpStatusCode.Accepted);

        (await fixture.ScalarAsync<string>(world.Database,
            $"select md5(t::text) from public.run t where id = '{world.Run:D}'")).ShouldBe(before);
        (await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.event_batch where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database,
        Guid User, Guid Org, Guid Project, Guid Run, Guid Report, string Parser) : IAsyncDisposable
    {
        internal Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("reprocess-boundary"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var parser = "junit-pytest-" + org.ToString("N");
        await RegisterAsync(database, org, parser, "1.0.0");
        await RegisterAsync(database, org, parser, "1.1.0");
        var run = await RunRowSeeder.SeedRunAsync(fixture, database, org);
        var world = new World(api, client, database, user, org, project, run, Guid.Empty, parser);
        return world with { Report = await SeedReportAsync(world, run) };
    }

    private async Task RegisterAsync(string database, Guid org, string parser, string version)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version, notes)
            values (@id, @org, @parser, @version, 'pytest-junitxml', 1, 'boundary coverage')
            on conflict (parser_id, version) do nothing
            """, connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("parser", parser);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static Task<ApiAnswer> PostAsync(World world, Guid report, string version = "1.1.0",
        string? key = null, Guid? run = null, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/runs/{run ?? world.Run}/reprocess", caller ?? world.Admin,
            new { raw_report_id = report, parser_version = version }, key ?? NewKey());

    private async Task<long> RequestsAsync(World world) => await fixture.ScalarAsync<long>(world.Database,
        $"select count(*) from public.raw_report_reprocess where org_id = '{world.Org:D}'");

    private async Task<Guid> SeedReportAsync(World world, Guid run)
    {
        var artifact = Guid.CreateVersion7();
        var report = Guid.CreateVersion7();
        var execution = artifact.ToString("N") + artifact.ToString("N");
        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync("""
            insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no,
                kind, object_key, media_type, checksum, sensitivity, state)
            values (@artifact, @org, @project, @run, @execution, 1, 'raw_report', @object_key,
                'application/xml', @checksum, 'normal', 'ready')
            """, ("artifact", artifact), ("org", world.Org), ("project", world.Project), ("run", run),
            ("execution", execution), ("object_key", $"org/{world.Org:D}/project/{world.Project:D}/run/{run:D}/attempt/{execution}/1/{artifact:D}"),
            ("checksum", "sha256:" + new string('c', 64)));
        await scope.ExecuteAsync("""
            insert into public.raw_report (id, org_id, run_id, artifact_id, producer_kind,
                parser_id, parser_version, outcome, observed_tests, observed_failures)
            values (@report, @org, @run, @artifact, 'pytest-junitxml', @parser, '1.0.0', 'mapped', 0, 0)
            """, ("report", report), ("org", world.Org), ("run", run), ("artifact", artifact), ("parser", world.Parser));
        await scope.CommitAsync();
        return report;
    }
}

// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>POST /v1/runs/{r}/reprocess</c> through the real host and a real PostgreSQL: a stored <c>raw_report</c> row and
/// the release asked to read it again. The route records one request row and one task in the same transaction, answers
/// 202 with the id the report row will carry, and never reads or changes run state. A report of another tenant or
/// another run is the one 404 <c>not_found</c> (00 §5.4: no existence oracle), and the handler applies the same
/// project-scope rule the comparison read does — no caller of this suite carries a project scope, so the project case
/// is covered by that read's suite rather than here. A report no release ever judged is 422
/// <c>raw_report_unprocessable</c>; a version that is unregistered, equal, older or unorderable is 422
/// <c>parser_version_unknown</c>. A replay of the key answers the stored id, the same key
/// with another body is 409 <c>idempotency_conflict</c>, and the same pair under a fresh key neither writes a second
/// request nor queues a second task.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReprocessEndpointTests
{
    private const string ProducerKind = "pytest-junitxml";
    private const string SourceVersion = "1.0.0";
    private const string NewerVersion = "1.1.0";
    private const string Checksum = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ReprocessTaskType = "ingest.reprocess_report";

    private readonly PostgresTenancyFixture _fixture;

    public ReprocessEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// One signed-in founder's world. <see cref="ParserId"/> is a release family of this org alone: the suite never
    /// adds a release to the global catalogue, because the resolver picks the newest visible release and a global
    /// one would change the parse of every later suite that expects the built-in release.
    /// </summary>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        string ParserId)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>Posts one reprocess request; <paramref name="key"/> null omits the mandatory header.</summary>
    private static Task<ApiAnswer> PostAsync(
        World world,
        Guid runId,
        Guid reportId,
        string version = NewerVersion,
        string? key = null,
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/runs/{runId}/reprocess",
            caller ?? world.Admin,
            new { raw_report_id = reportId, parser_version = version },
            key ?? NewKey());

    // ------------------------------------------------------------------ acceptance

    [Fact]
    public async Task An_accepted_request_answers_the_parsing_id_and_queues_exactly_one_task()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.Accepted);
        var parsingId = answer.Id("parsing_id");
        parsingId.ShouldNotBe(Guid.Empty);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var stored = await scope.ScalarAsync<Guid>(
            "select id from public.raw_report_reprocess where org_id = @org and artifact_id = @artifact",
            ("org", world.Org),
            ("artifact", await ArtifactOfAsync(world, reportId)));
        stored.ShouldBe(parsingId);
        (await ReprocessTasksAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_replayed_key_answers_the_same_parsing_id()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);
        var key = NewKey();

        var first = await PostAsync(world, runId, reportId, key: key);
        var replay = await PostAsync(world, runId, reportId, key: key);

        first.Status.ShouldBe(HttpStatusCode.Accepted);
        replay.Status.ShouldBe(HttpStatusCode.Accepted);
        replay.Id("parsing_id").ShouldBe(first.Id("parsing_id"));
        (await ReprocessTasksAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_a_conflict()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);
        var key = NewKey();

        (await PostAsync(world, runId, reportId, key: key)).Status.ShouldBe(HttpStatusCode.Accepted);
        var other = await PostAsync(world, runId, reportId, version: "1.2.0", key: key);

        other.Status.ShouldBe(HttpStatusCode.Conflict);
        other.ErrorCode.ShouldBe("idempotency_conflict");
    }

    [Fact]
    public async Task The_same_pair_under_a_fresh_key_answers_the_stored_id_and_queues_no_second_task()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var first = await PostAsync(world, runId, reportId);
        var second = await PostAsync(world, runId, reportId);

        second.Status.ShouldBe(HttpStatusCode.Accepted);
        second.Id("parsing_id").ShouldBe(first.Id("parsing_id"));
        (await ReprocessTasksAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_report_already_parsed_with_the_requested_release_is_answered_with_its_own_id()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId, version: NewerVersion);

        // The row exists, so this release is not newer: whatever the route answers, it never records a request.
        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("parser_version_unknown");
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task Another_orgs_run_is_the_one_not_found()
    {
        await using var world = await StartAsync();
        await using var other = await StartAsync();
        var foreignRun = await SeedRunAsync(other);
        var reportId = await SeedReportAsync(other, foreignRun);

        var answer = await PostAsync(world, foreignRun, reportId);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task A_report_of_another_run_is_the_one_not_found()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var otherRun = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, otherRun);

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_report_is_the_one_not_found()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await PostAsync(world, runId, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task An_unsupported_report_is_unprocessable()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId, outcome: "unsupported");

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("raw_report_unprocessable");
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    [InlineData("2.0.0-rc.1")]
    [InlineData("9.9.9")]
    public async Task An_equal_older_unorderable_or_unregistered_version_is_unknown(string version)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var answer = await PostAsync(world, runId, reportId, version: version);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("parser_version_unknown");
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task A_request_without_a_key_is_refused_before_the_handler()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var answer = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/runs/{runId}/reprocess",
            world.Admin,
            new { raw_report_id = reportId, parser_version = NewerVersion });

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_without_run_create_is_forbidden()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        // A user the org never admitted holds no role at all, so the C2 stage refuses before the handler's read.
        var stranger = Guid.CreateVersion7();
        var answer = await PostAsync(
            world,
            runId,
            reportId,
            caller: TenancyApiFactory.Resolved(user: stranger, orgHeader: world.Org));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await ReprocessTasksAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ run state and accounting

    [Fact]
    public async Task A_canceled_run_is_accepted_and_its_state_is_unchanged()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);
        await SetRunStateAsync(world, runId, "canceled");

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.Accepted);
        (await RunStateAsync(world, runId)).ShouldBe("canceled");
        (await ReprocessTasksAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task A_reprocess_writes_no_usage_ledger_and_no_event_batch_row()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        (await PostAsync(world, runId, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var ledger = await scope.ScalarAsync<long>(
            "select count(*) from public.usage_ledger_entry where org_id = @org",
            ("org", world.Org));
        var batches = await scope.ScalarAsync<long>(
            "select count(*) from public.event_batch where org_id = @org",
            ("org", world.Org));

        ledger.ShouldBe(0);
        batches.ShouldBe(0);
    }

    // ------------------------------------------------------------------ seeding

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("reprocess"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var parserId = await SeedReleasesAsync(database, org);
        return new World(api, client, database, user, org, project, parserId);
    }

    /// <summary>
    /// Two project-owned releases of one org-only release family: the version the seeded report carries and the newer
    /// one the requests ask for. <c>report_parser</c> is owner-written (0075 revokes INSERT from the app role), so
    /// they are seeded as the migration owner; keeping them org-owned leaves the global catalogue — and therefore
    /// every other suite's parsing — untouched.
    /// </summary>
    private async Task<string> SeedReleasesAsync(string database, Guid org)
    {
        // The whole org id, not a prefix: UUIDv7's leading digits are timestamp bits two orgs created seconds
        // apart share, and `report_parser` is uniquely keyed on `(parser_id, version)` globally — a truncated id
        // would collide with a sibling suite's and the later org would find no release visible to it.
        var parserId = "junit-pytest-" + org.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version, notes)
            values (@id, @org_id, @parser_id, @version, @producer_kind, 1, 'reprocess suite: this org only')
            on conflict (parser_id, version) do nothing
            """,
            connection);
        command.Parameters.AddWithValue("org_id", org);
        command.Parameters.AddWithValue("parser_id", parserId);
        command.Parameters.AddWithValue("producer_kind", ProducerKind);

        var id = command.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Uuid);
        var version = command.Parameters.Add("version", NpgsqlTypes.NpgsqlDbType.Text);

        foreach (var release in new[] { SourceVersion, NewerVersion })
        {
            id.Value = Guid.CreateVersion7();
            version.Value = release;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return parserId;
    }

    private async Task<Guid> SeedRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    /// <summary>
    /// One report whose artifact belongs to <paramref name="runId"/>: the row the route reprocesses, written at the
    /// release the migration seeds so a newer version is a real reprocess. An <c>unsupported</c> outcome carries no
    /// parser pair and the refusal's code word — <c>raw_report_reason_check</c> admits no reasonless refusal — and
    /// the reason is the resolver's own <c>unknown_producer_kind</c>.
    /// </summary>
    private async Task<Guid> SeedReportAsync(
        World world,
        Guid runId,
        string version = SourceVersion,
        string outcome = "mapped")
    {
        var reportId = Guid.CreateVersion7();
        var artifactId = Guid.CreateVersion7();
        var executionKey = artifactId.ToString("N") + artifactId.ToString("N");
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.artifact
            (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum,
            sensitivity, state)
            values (@id, @org, @project, @run, @execution_key, 1, 'raw_report', @object_key, 'application/xml',
            @checksum, 'normal', 'ready')
            """,
            ("id", artifactId),
            ("org", world.Org),
            ("project", world.Project),
            ("run", runId),
            ("execution_key", executionKey),
            ("object_key", $"org/{world.Org:D}/project/{world.Project:D}/run/{runId:D}/attempt/{executionKey}/1/{artifactId:D}"),
            ("checksum", Checksum))).ShouldBe(1);
        (await scope.ExecuteAsync(
            """
            insert into public.raw_report
            (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome, reason,
            observed_tests, observed_failures)
            values (@id, @org, @run, @artifact, @producer_kind, @parser_id::text, @parser_version::text, @outcome,
            @reason::text, 0, 0)
            """,
            ("id", reportId),
            ("org", world.Org),
            ("run", runId),
            ("artifact", artifactId),
            ("producer_kind", ProducerKind),
            ("parser_id", outcome == "unsupported" ? (object)DBNull.Value : world.ParserId),
            ("parser_version", outcome == "unsupported" ? (object)DBNull.Value : version),
            ("outcome", outcome),
            ("reason", outcome == "unsupported" ? "unknown_producer_kind" : (object)DBNull.Value))).ShouldBe(1);
        await scope.CommitAsync();
        return reportId;
    }

    private async Task<Guid> ArtifactOfAsync(World world, Guid reportId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        return await scope.ScalarAsync<Guid>(
            "select artifact_id from public.raw_report where id = @id",
            ("id", reportId));
    }

    private async Task<long> ReprocessTasksAsync(World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        return await scope.ScalarAsync<long>(
            "select count(*) from public.outbox where org_id = @org and task_type = @task_type",
            ("org", world.Org),
            ("task_type", ReprocessTaskType));
    }

    private async Task SetRunStateAsync(World world, Guid runId, string state)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "update public.run set state = @state where id = @id",
            ("state", state),
            ("id", runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    private async Task<string?> RunStateAsync(World world, Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        return await scope.ScalarAsync<string>("select state from public.run where id = @id", ("id", runId));
    }
}

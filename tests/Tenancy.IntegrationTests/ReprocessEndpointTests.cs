// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>POST /v1/runs/{r}/reprocess</c> through the real host and a real PostgreSQL: a stored <c>raw_report</c> row and
/// the release asked to read it again. The route records one request row and one task in the same transaction, answers
/// 202 with the id the report row will carry, and never reads or changes run state. A report of another tenant or
/// another run is the one 404 <c>not_found</c> (no existence oracle), and the handler applies the same
/// project-scope rule the comparison read does — <c>run:create</c> is inside the service-role ceiling, so a
/// project-bound service credential is accepted for a report of its own project and gets that 404 for another. A
/// report no release ever judged is 422
/// <c>raw_report_unprocessable</c>; a version that is unregistered, equal, older or unorderable is 422
/// <c>parser_version_unknown</c>. A replay of the key answers the stored id, the same key
/// with another body is 409 <c>idempotency_conflict</c>, and the same pair under a fresh key neither writes a second
/// request nor queues a second task. A body over the route's 4 KiB cap is 413 <c>payload_too_large</c> before it is
/// parsed.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class ReprocessEndpointTests
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

    /// <summary>
    /// One report row for an artifact the caller already holds, at <paramref name="version"/>: the row an earlier
    /// reprocess with that release wrote. The table's natural key is (artifact, parser pair), so this pair answers as
    /// already parsed and the route must return its id instead of queueing a second task.
    /// </summary>
    private async Task<Guid> SeedParsedAsync(World world, Guid runId, Guid artifactId, string version)
    {
        var reportId = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.raw_report
            (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome,
            observed_tests, observed_failures)
            values (@id, @org, @run, @artifact, @producer_kind, @parser_id, @parser_version, 'mapped', 0, 0)
            """,
            ("id", reportId),
            ("org", world.Org),
            ("run", runId),
            ("artifact", artifactId),
            ("producer_kind", ProducerKind),
            ("parser_id", world.ParserId),
            ("parser_version", version))).ShouldBe(1);
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

    /// <summary>The audit rows of one action, as <c>object_type:object_id</c>, in the order they were written.</summary>
    private async Task<string[]> AuditedObjectsAsync(World world, string action)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        return await scope.ScalarAsync<string[]>(
            """
            select array_agg(object_type || ':' || object_id order by seq)
            from public.audit_log
            where org_id = @org and action = @action
            """,
            ("org", world.Org),
            ("action", action)) ?? [];
    }

    /// <summary>
    /// Posts one body verbatim, with the mandatory key: a body padded to a byte count has no serialised form, and the
    /// cap is refused only after the key is present.
    /// </summary>
    private static async Task<ApiAnswer> PostRawAsync(World world, Guid runId, string raw)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{runId}/reprocess")
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", NewKey());
        world.Admin(request.Headers);

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>A legal reprocess body of exactly <paramref name="totalBytes"/> bytes: trailing whitespace is legal JSON.</summary>
    private static string PaddedBody(Guid reportId, int totalBytes)
    {
        var json = $$"""{"raw_report_id":"{{reportId:D}}","parser_version":"{{NewerVersion}}"}""";
        var length = Encoding.UTF8.GetByteCount(json);

        length.ShouldBeLessThan(totalBytes, "the pad must leave room for the whole body");

        return json + new string(' ', totalBytes - length);
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

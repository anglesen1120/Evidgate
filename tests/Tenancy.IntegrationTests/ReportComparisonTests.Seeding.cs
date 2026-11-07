using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReportComparisonTests
{
    // ------------------------------------------------------------------ requests

    /// <summary>Reads one page of a report's comparison with the default <c>run:read</c> caller.</summary>
    private static async Task<ApiAnswer> GetAsync(World world, Guid runId, Guid reportId, int? limit = null, string? cursor = null)
    {
        var url = new StringBuilder(string.Create(
            CultureInfo.InvariantCulture,
            $"/v1/runs/{runId}/reports/{reportId}/comparison"));
        var separator = '?';
        if (limit is { } value)
        {
            url.Append(separator).Append(CultureInfo.InvariantCulture, $"limit={value}");
            separator = '&';
        }

        if (cursor is not null)
        {
            url.Append(separator).Append("cursor=").Append(Uri.EscapeDataString(cursor));
        }

        return await world.Client.SendAsync(HttpMethod.Get, url.ToString(), world.Admin);
    }

    private static string[] Keys(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private static JsonElement[] Cases(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("cases").EnumerateArray()];

    private static string[] CaseRefs(ApiAnswer answer) =>
        [.. Cases(answer).Select(item => item.GetProperty("case_ref").GetString()!)];

    private static string[] Codes(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("discrepancies").EnumerateArray().Select(item => item.GetString()!)];

    // ------------------------------------------------------------------ seeding

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("comparison"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        return new World(api, client, database, user, org, project);
    }

    private async Task<Guid> SeedRunAsync(World world) =>
        await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

    /// <summary>
    /// One committed report: its artifact row (the execution key the parse derives), the producer slot, the
    /// <c>raw_report</c> row and one <c>test.finished</c> event per seeded case. A case label mints the execution key,
    /// so two events of one label are two events of one case.
    /// </summary>
    private async Task<SeededReport> SeedReportAsync(
        World world,
        Guid runId,
        string outcome = "mapped",
        string? reason = null,
        int? declaredTests = null,
        int? declaredFailures = null,
        int? observedTests = null,
        int? observedFailures = null,
        string discrepancies = "[]",
        EventSeed[]? events = null,
        Guid? org = null,
        Guid? project = null)
    {
        var seeded = events ?? [];
        var reportId = Guid.CreateVersion7();
        var artifactId = Guid.CreateVersion7();
        var producerId = RawReportProducer.IdFor(artifactId);
        var artifactExecutionKey = artifactId.ToString("N") + artifactId.ToString("N");
        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            org ?? world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.artifact
            (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum,
            sensitivity, state)
            values (@id, @org, @project, @run, @execution_key, 1, 'raw_report', @object_key, 'application/xml',
            @checksum, 'normal', 'ready')
            """,
            ("id", artifactId),
            ("org", org ?? world.Org),
            ("project", project ?? world.Project),
            ("run", runId),
            ("execution_key", artifactExecutionKey),
            ("object_key", string.Create(
                CultureInfo.InvariantCulture,
                $"org/{org ?? world.Org:D}/project/{project ?? world.Project:D}/run/{runId:D}/attempt/{artifactExecutionKey}/1/{artifactId:D}")),
            ("checksum", Checksum))).ShouldBe(1);
        (await scope.ExecuteAsync(
            """
            insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
            values (@id, @org, @run,
            (select count(*)::int + 1 from public.producer_slot where org_id = @org and run_id = @run), @producer)
            """,
            ("id", Guid.CreateVersion7()),
            ("org", org ?? world.Org),
            ("run", runId),
            ("producer", producerId))).ShouldBe(1);
        (await scope.ExecuteAsync(
            """
            insert into public.raw_report
            (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome, reason,
            declared_tests, declared_failures, observed_tests, observed_failures, discrepancies)
            values (@id, @org, @run, @artifact, @producer_kind, @parser_id::text, @parser_version::text, @outcome,
            @reason::text, @declared_tests::integer, @declared_failures::integer, @observed_tests::integer,
            @observed_failures::integer, @discrepancies::jsonb)
            """,
            ("id", reportId),
            ("org", org ?? world.Org),
            ("run", runId),
            ("artifact", artifactId),
            ("producer_kind", ProducerKind),
            ("parser_id", OrNull(outcome == "unsupported" ? null : ParserId)),
            ("parser_version", OrNull(outcome == "unsupported" ? null : ParserVersion)),
            ("outcome", outcome),
            ("reason", OrNull(reason)),
            ("declared_tests", OrNull(declaredTests)),
            ("declared_failures", OrNull(declaredFailures)),
            ("observed_tests", OrNull(observedTests ?? seeded.Length)),
            ("observed_failures", OrNull(observedFailures ?? 0)),
            ("discrepancies", discrepancies))).ShouldBe(1);
        await SeedEventsAsync(scope, org ?? world.Org, runId, producerId, seeded);

        await scope.CommitAsync();
        return new SeededReport(reportId, artifactId);
    }

    /// <summary>One <c>test.finished</c> event per seeded case, all under the producer whose slot already exists.</summary>
    private static async Task SeedEventsAsync(
        TenantScope scope,
        Guid org,
        Guid runId,
        string producerId,
        EventSeed[] seeded)
    {
        foreach (var item in seeded)
        {
            (await scope.ExecuteAsync(
                """
                insert into public.event
                (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type,
                raw_status, normalized_status, payload_hash)
                values (@id, @org, @run, @event_id, @producer, @sequence, @execution_key, 1, 'test.finished',
                @raw_status, 'passed', @payload_hash)
                """,
                ("id", Guid.CreateVersion7()),
                ("org", org),
                ("run", runId),
                ("event_id", Guid.CreateVersion7()),
                ("producer", producerId),
                ("sequence", item.Sequence),
                ("execution_key", ExecutionKeyFor(item.CaseRef)),
                ("raw_status", item.RawStatus),
                ("payload_hash", PayloadHash))).ShouldBe(1);
        }
    }

    /// <summary>
    /// Seeded comparison state pins a distinct org-owned release without changing the shared built-in catalogue.
    /// The report, request, synthetic slot and case events support comparison reads, not a worker lifecycle replay.
    /// </summary>
    private async Task<SeededReport> SeedReprocessAsync(
        World world,
        Guid runId,
        SeededReport source,
        EventSeed[]? events = null)
    {
        var seeded = events ?? [];
        var parsingId = Guid.CreateVersion7();
        var parserId = ReprocessParserId(world);

        await SeedReleaseAsync(world, parserId);
        await SeedReprocessRowsAsync(world, runId, source, parsingId, parserId, seeded);

        return new SeededReport(parsingId, source.ArtifactId);
    }

    /// <summary>
    /// Seeded comparison state connects the report and request to a synthetic slot and its events.
    /// The slot is left open, so no declared close is seeded; the comparison read joins on <c>producer_id</c> alone.
    /// </summary>
    private async Task SeedReprocessRowsAsync(
        World world,
        Guid runId,
        SeededReport source,
        Guid parsingId,
        string parserId,
        EventSeed[] seeded)
    {
        var producerId = ReprocessReportProducer.IdFor(parsingId);

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.raw_report
            (id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version, outcome, reason,
            declared_tests, declared_failures, observed_tests, observed_failures, discrepancies)
            values (@id, @org, @run, @artifact, @producer_kind, @parser_id::text, @parser_version::text, 'mapped', null,
            @declared_tests::integer, 0, @observed_tests, 0, '[]'::jsonb)
            """,
            ("id", parsingId),
            ("org", world.Org),
            ("run", runId),
            ("artifact", source.ArtifactId),
            ("producer_kind", ProducerKind),
            ("parser_id", parserId),
            ("parser_version", ReprocessVersion),
            ("declared_tests", OrNull(seeded.Length)),
            ("observed_tests", seeded.Length))).ShouldBe(1);
        (await scope.ExecuteAsync(
            """
            insert into public.raw_report_reprocess
            (id, org_id, run_id, artifact_id, source_raw_report_id, parser_id, parser_version)
            values (@id, @org, @run, @artifact, @source, @parser_id, @parser_version)
            """,
            ("id", parsingId),
            ("org", world.Org),
            ("run", runId),
            ("artifact", source.ArtifactId),
            ("source", source.Id),
            ("parser_id", parserId),
            ("parser_version", ReprocessVersion))).ShouldBe(1);
        (await scope.ExecuteAsync(
            """
            insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
            values (@id, @org, @run,
            (select count(*)::int + 1 from public.producer_slot where org_id = @org and run_id = @run), @producer)
            """,
            ("id", Guid.CreateVersion7()),
            ("org", world.Org),
            ("run", runId),
            ("producer", producerId))).ShouldBe(1);
        await SeedEventsAsync(scope, world.Org, runId, producerId, seeded);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The org-owned pinned release is named and dated distinctly for comparison state, written with owner privileges.
    /// Numeric version order, not registration recency, decides resolver selection.
    /// </summary>
    private async Task SeedReleaseAsync(World world, string parserId) =>
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.report_parser "
            + "(id, org_id, parser_id, version, producer_kind, schema_version, notes, created_at) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{parserId}', '{ReprocessVersion}', '{ProducerKind}', 1, "
            + "'comparison suite: this org only', '2000-01-01T00:00:00Z') "
            + "on conflict (parser_id, version) do nothing");

    /// <summary>The org-owned release family this world pins its reprocesses to: unique per org, visible to it alone.</summary>
    private static string ReprocessParserId(World world) =>
        "junit-pytest-" + world.Org.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The execution key a seeded case carries: one per case label, so two events of a label share it.</summary>
    private static string ExecutionKeyFor(string caseRef) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(caseRef)));

    private static object OrNull(string? value) => value ?? (object)DBNull.Value;

    private static object OrNull(int? value) => value ?? (object)DBNull.Value;
}

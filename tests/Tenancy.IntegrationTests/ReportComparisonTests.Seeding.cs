using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
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
        var producerId = "junit-" + artifactId.ToString("D", CultureInfo.InvariantCulture);
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
            ("observed_failures", OrNull(observedFailures)),
            ("discrepancies", discrepancies))).ShouldBe(1);
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
                ("org", org ?? world.Org),
                ("run", runId),
                ("event_id", Guid.CreateVersion7()),
                ("producer", producerId),
                ("sequence", item.Sequence),
                ("execution_key", ExecutionKeyFor(item.CaseRef)),
                ("raw_status", item.RawStatus),
                ("payload_hash", PayloadHash))).ShouldBe(1);
        }

        await scope.CommitAsync();
        return new SeededReport(reportId, artifactId);
    }

    /// <summary>The execution key a seeded case carries: one per case label, so two events of a label share it.</summary>
    private static string ExecutionKeyFor(string caseRef) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(caseRef)));

    private static object OrNull(string? value) => value ?? (object)DBNull.Value;

    private static object OrNull(int? value) => value ?? (object)DBNull.Value;
}

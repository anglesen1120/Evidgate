using System.Globalization;
using System.Net;
using Evidgate.Modules.Evidence.Application;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    /// <summary>
    /// The comparison half of the reprocess path: the report the run already holds and the report a production
    /// reprocess wrote are both read back through the real route, and each answers its own row, release and counters.
    /// </summary>
    /// <remarks>
    /// The reprocessed row is written by the production handler from a request row, never seeded. Both releases map the
    /// one case identically, so a wrong-producer selection would look the same here; what is distinguished is the
    /// producer predicate itself — the run holds one event of that key per producer, and each page must return one.
    /// </remarks>
    [Fact]
    public async Task Comparison_ProductionReprocess_ServesTheReprocessedRevisionBesideTheSource()
    {
        await using var world = await StartAsync();
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var original = await ParseOriginalReportAsync(world, artifactId);
        original.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        original.Events.ShouldBe(1);
        var sourceReportId = await SourceReportIdAsync(world, artifactId);
        var first = await FreezeFirstRevisionAsync(world, artifactId, sourceReportId);

        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var reprocessed = await ReprocessAsync(world, parsingId);

        reprocessed.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        reprocessed.Events.ShouldBe(1);
        await ConsumeEnqueuedReconcileAsync(
            world, "reprocess-" + parsingId.ToString("D", CultureInfo.InvariantCulture));
        await AssertFirstRevisionUntouchedAsync(world, artifactId, sourceReportId, first);
        await AssertSecondRevisionAsync(world, first);
        (await KeyedEventCountAsync(world))
            .ShouldBe(2, "the run holds one event of this key from each producer, so a page of one is the predicate's doing");

        await using var registry = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(world.Database));
        using var client = registry.CreateClient();
        var founder = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select user_id from public.membership where org_id = '{world.Org:D}' order by created_at, id limit 1");
        var viewer = await AddMemberAsync(
            _fixture,
            client,
            world.Database,
            world.Org,
            TenancyApiFactory.Resolved(user: founder, orgHeader: world.Org),
            "viewer");
        var caller = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);

        var source = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{world.RunId:D}/reports/{sourceReportId:D}/comparison",
            caller);
        var revised = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{world.RunId:D}/reports/{parsingId:D}/comparison",
            caller);

        AssertProductionComparison(source, world.RunId, artifactId, sourceReportId, SourceVersion);
        AssertProductionComparison(revised, world.RunId, artifactId, parsingId, PinVersion);
    }

    /// <summary>The run's events under the one execution key: one from the original parse, one from the reprocess.</summary>
    private Task<long> KeyedEventCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.event where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' and execution_key = '{ExecutionKey}'");

    /// <summary>One report's stored row as the route serves it: its own release, its counters and its own one case.</summary>
    private static void AssertProductionComparison(
        ApiAnswer answer, Guid runId, Guid artifactId, Guid reportId, string version)
    {
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("report_id").GetGuid().ShouldBe(reportId);
        answer.Body.GetProperty("run_id").GetGuid().ShouldBe(runId);
        answer.Body.GetProperty("raw_report_artifact_id").GetGuid().ShouldBe(artifactId);
        answer.Body.GetProperty("producer_kind").GetString().ShouldBe(ProducerKind);
        answer.Body.GetProperty("parser_id").GetString().ShouldBe(ParserId);
        answer.Body.GetProperty("parser_version").GetString().ShouldBe(version);
        var summary = answer.Body.GetProperty("summary");
        summary.GetProperty("status").GetString().ShouldBe("mapped");
        summary.GetProperty("declared_tests").GetInt32().ShouldBe(1);
        summary.GetProperty("observed_tests").GetInt32().ShouldBe(1);
        var cases = answer.Body.GetProperty("cases").EnumerateArray().ToArray();
        cases.ShouldHaveSingleItem();
        cases[0].GetProperty("case_ref").GetString().ShouldBe(ExecutionKey);
        cases[0].GetProperty("declared_status").GetString().ShouldBe("passed");
        cases[0].GetProperty("observed_events").GetInt32().ShouldBe(1);
    }
}

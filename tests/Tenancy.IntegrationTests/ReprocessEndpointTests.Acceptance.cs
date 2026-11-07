// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using Evidgate.Modules.Evidence.Endpoints;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessEndpointTests
{
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
        var reportId = await SeedReportAsync(world, runId);
        var artifactId = await ArtifactOfAsync(world, reportId);

        // The pair already exists: the release re-read these bytes before and wrote the row this id names.
        var parsedId = await SeedParsedAsync(world, runId, artifactId, NewerVersion);

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.Accepted);
        answer.Id("parsing_id").ShouldBe(parsedId);
        (await ReprocessTasksAsync(world)).ShouldBe(0, "the work exists, so nothing is queued");
    }

    [Fact]
    public async Task A_body_over_the_route_cap_is_payload_too_large_and_records_nothing()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        // Exactly at the cap is read and accepted; one byte more is refused before it is parsed.
        var atCap = await PostRawAsync(world, runId, PaddedBody(reportId, ReprocessLimits.MaxBodyBytes));
        var overCap = await PostRawAsync(world, runId, PaddedBody(reportId, ReprocessLimits.MaxBodyBytes + 1));

        overCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overCap.ErrorCode.ShouldBe("payload_too_large");
        overCap.Body.GetRawText().ShouldNotContain(NewerVersion, Case.Sensitive, "the refusal echoes nothing of the body");
        (await ReprocessTasksAsync(world)).ShouldBe(1, "only the body at the cap was read and accepted");

        atCap.Status.ShouldBe(HttpStatusCode.Accepted, atCap.ErrorMessage);
    }

    [Fact]
    public async Task An_accepted_request_audits_the_new_report_id_and_a_replay_audits_nothing()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);
        var key = NewKey();

        var first = await PostAsync(world, runId, reportId, key: key);
        var replay = await PostAsync(world, runId, reportId, key: key);

        first.Status.ShouldBe(HttpStatusCode.Accepted);
        replay.Id("parsing_id").ShouldBe(first.Id("parsing_id"));

        // [AuditedAction] records the accepted write against the report id it will create; the replay wrote nothing,
        // so its entry is suppressed and the action keeps one row.
        (await AuditedObjectsAsync(world, "raw_report.reprocess"))
            .ShouldBe(new[] { $"raw_report_reprocess:{first.Id("parsing_id"):D}" });
    }
}

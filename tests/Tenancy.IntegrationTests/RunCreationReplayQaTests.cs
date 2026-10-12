// 05.2.1-T5 QA (independent): negative, boundary and ordering cases for the replay members of POST .../runs.
// Complements RunCreationReplayTests.cs; it does not repeat its cases.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunCreationReplayQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunCreationReplayQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static string Body(Guid manifestId, string extra = "") =>
        "{\"manifest_id\":\"" + manifestId + "\"" + extra + "}";

    private static JsonElement Detail(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0];

    private static string? DetailPath(ApiAnswer answer) => Detail(answer).GetProperty("path").GetString();

    private static string? DetailReason(ApiAnswer answer) => Detail(answer).GetProperty("reason").GetString();

    private async Task<long[]> CountsAsync(RunCreationTests.World world) =>
    [
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.planned_execution"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.producer_slot"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.outbox where task_type = 'orchestrator.run.created'"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.idempotency_record where operation = 'run:create'"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.audit_log where action = 'run.create'"),
    ];

    // ------------------------------------------------------------------ purpose is a case-sensitive exact match

    [Fact]
    public async Task Purpose_IsCaseSensitiveAndExact_OtherSpellingsAreRequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        foreach (var spelling in new[] { "Replay", "REPLAY", "replay ", " replay", "Regular", "" })
        {
            var answer = await RunCreationTests.CreateRunAsync(
                world,
                Body(world.ManifestId, ",\"purpose\":\"" + spelling + "\""));
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"purpose \"{spelling}\"");
            answer.ErrorCode.ShouldBe("request_invalid", $"purpose \"{spelling}\"");
            DetailPath(answer).ShouldBe("/purpose", $"purpose \"{spelling}\"");
        }

        (await CountsAsync(world)).ShouldBe(before, "a refused body writes nothing");
    }

    // ------------------------------------------------------------------ ordering: body shape before replay refusal

    [Fact]
    public async Task Replay_MissingManifestId_IsRequestInvalidNotGrantInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await RunCreationTests.CreateRunAsync(
            world,
            "{\"purpose\":\"replay\",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\"}");
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPath(answer).ShouldBe("/manifest_id");
    }

    [Fact]
    public async Task Replay_UnknownManifest_IsRefusedBeforeManifestReadNotNotFound()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        // AC8 refuses before any manifest read, so an unknown manifest id is replay_grant_invalid, not 404 not_found.
        var answer = await RunCreationTests.CreateRunAsync(
            world,
            Body(Guid.CreateVersion7(), ",\"purpose\":\"replay\""));
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("replay_grant_invalid");

        (await CountsAsync(world)).ShouldBe(before);
    }

    [Fact]
    public async Task Replay_ReleaseNonStringOrEmpty_IsReleaseRequestInvalidNotReplayCode()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        // The release shape check runs before the replay checks, so a malformed release is request_invalid even on replay.
        var numeric = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"release\":7"));
        numeric.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        numeric.ErrorCode.ShouldBe("request_invalid");
        DetailPath(numeric).ShouldBe("/release");

        var empty = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"release\":\"\""));
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        empty.ErrorCode.ShouldBe("request_invalid");
        DetailPath(empty).ShouldBe("/release");

        (await CountsAsync(world)).ShouldBe(before);
    }

    [Fact]
    public async Task Replay_ComponentsNotAnObject_IsRefusedBeforeComponentChecks()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        // The replay refusal sits before the components check, so a bad components value still returns replay_grant_invalid.
        var answer = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"components\":\"not-an-object\""));
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("replay_grant_invalid");

        (await CountsAsync(world)).ShouldBe(before);
    }

    // ------------------------------------------------------------------ replay_grant_id parsing boundaries

    [Fact]
    public async Task ReplayGrantId_NilAndUppercaseUuid_ParseThenRefusedAsGrantInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var nil = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"00000000-0000-0000-0000-000000000000\""));
        nil.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        nil.ErrorCode.ShouldBe("replay_grant_invalid", "a nil UUID is a valid UUID shape, refused by the grant check");

        var upper = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"" + Guid.CreateVersion7().ToString().ToUpperInvariant() + "\""));
        upper.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        upper.ErrorCode.ShouldBe("replay_grant_invalid");
    }

    [Fact]
    public async Task ReplayGrantId_NonStringOrEmpty_IsRequestInvalidOnGrantPath()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        foreach (var raw in new[] { "7", "\"\"", "null", "true" })
        {
            var answer = await RunCreationTests.CreateRunAsync(
                world,
                Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":" + raw));
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"replay_grant_id {raw}");
            answer.ErrorCode.ShouldBe("request_invalid", $"replay_grant_id {raw}");
            DetailPath(answer).ShouldBe("/replay_grant_id", $"replay_grant_id {raw}");
        }

        (await CountsAsync(world)).ShouldBe(before);
    }

    // ------------------------------------------------------------------ duplicate member names must never smuggle a replay

    [Fact]
    public async Task DuplicatePurposeMembers_NeverStoreAReplayRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        // JsonDocument.Parse accepts duplicate names. Whichever value the handler reads, no run may ever store purpose=replay
        // (replay is refused until 08.4.3), and a replay-first body must not reach the create path.
        var replayFirst = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"purpose\":\"regular\""));
        var regularFirst = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"regular\",\"purpose\":\"replay\""));

        foreach (var answer in new[] { replayFirst, regularFirst })
        {
            if (answer.Status == HttpStatusCode.Created)
            {
                continue;
            }

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run where purpose = 'replay'"))
            .ShouldBe(0);
    }

    // ------------------------------------------------------------------ the refusals leave the key and the regular path intact

    [Fact]
    public async Task AfterReplayRefusals_RegularRunStillCreatesAndStoresRegular()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var refused = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\""));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var regular = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        regular.Status.ShouldBe(HttpStatusCode.Created, regular.ErrorMessage);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run where purpose = 'regular' and replay_grant_id is null"))
            .ShouldBe(1);
    }
}

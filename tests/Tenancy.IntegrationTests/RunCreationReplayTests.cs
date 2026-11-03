// 05.2.1-T5 (AC8/AC9): the replay purpose of POST /v1/projects/{p}/runs through the real host and a real PostgreSQL.
// Request bodies are written inline so each test reads as the request it sends.
//
// T5.1 statement: the purpose/replay_grant_id bound CHECKs of migration 0031 are already pinned by
// RunSchemaTests.Purpose_ReplayWithoutGrant_FailsCheck (run_replay_grant_check, RunSchemaTests.cs:551-562) and
// RunSchemaTests.Purpose_Unknown_FailsCheck (run_purpose_check, RunSchemaTests.cs:564-575), and the app role's missing
// UPDATE grant on run by RunSchemaTests.AppRole_CannotUpdateOrDelete_Run (RunSchemaTests.cs:379-388); this file does
// not duplicate them.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T5: <c>purpose</c> (<c>regular</c> | <c>replay</c>) and <c>replay_grant_id</c> on the run-create request.
/// Until 08.4.3 lands a grant store every replay is refused — <c>replay_grant_invalid</c> (AC8) — and a replay that
/// also names a release is <c>replay_release_not_allowed</c> (AC9). Both are 422 refusals that write nothing and leave
/// the <c>Idempotency-Key</c> free; the regular path is unchanged and stores <c>purpose = 'regular'</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunCreationReplayTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunCreationReplayTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One run-create body: the manifest id plus an optional raw member list (leading comma included).</summary>
    private static string Body(Guid manifestId, string extra = "") =>
        "{\"manifest_id\":\"" + manifestId + "\"" + extra + "}";

    /// <summary>The first <c>error.details</c> entry of a refused request.</summary>
    private static JsonElement Detail(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0];

    /// <summary>The 00 §9.2 <c>error.details[0].path</c>.</summary>
    private static string? DetailPath(ApiAnswer answer) => Detail(answer).GetProperty("path").GetString();

    /// <summary>The 00 §9.2 <c>error.details[0].reason</c>.</summary>
    private static string? DetailReason(ApiAnswer answer) => Detail(answer).GetProperty("reason").GetString();

    /// <summary>
    /// The six row counts a refused request must leave untouched, scoped to the world's own tenant because the
    /// database is shared: run, planned execution, producer slot, the run-create outbox row, the <c>run:create</c>
    /// idempotency record and the <c>run.create</c> audit row.
    /// </summary>
    private async Task<long[]> CountsAsync(RunCreationTests.World world) =>
    [
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.planned_execution where org_id = '{world.Org:D}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.producer_slot where org_id = '{world.Org:D}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = 'orchestrator.run.created'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.idempotency_record where org_id = '{world.Org:D}' and operation = 'run:create'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'run.create'"),
    ];

    // ------------------------------------------------------------------ AC8

    [Fact]
    public async Task CreateRun_ReplayGrant_InvalidOrExpired_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        // (a) A random grant id: there is no grant store until 08.4.3, so no id — random or not — can match.
        var random = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\""));
        random.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        random.ErrorCode.ShouldBe("replay_grant_invalid");

        // (b) No grant id at all is the same refusal, not a body error.
        var missing = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\""));
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.ErrorCode.ShouldBe("replay_grant_invalid");

        // (c) The nil UUID: a well-formed shape that is never issued. It is parsed (so it is not request_invalid) and
        // then refused by the grant check, the same as any other well-formed id.
        var nil = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"00000000-0000-0000-0000-000000000000\""));
        nil.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        nil.ErrorCode.ShouldBe("replay_grant_invalid");

        before.ShouldAllBe(count => count == 0);
        (await CountsAsync(world)).ShouldBe(before, "a refused replay writes no run, planned execution, slot, outbox, idempotency or audit row");
    }

    // ------------------------------------------------------------------ AC9

    [Fact]
    public async Task CreateRun_ReplayWithRelease_422ReplayReleaseNotAllowed()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        // A replay names no release: the release code wins over the grant code (release present, even well formed).
        var withGrant = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"release\":\"v1.0\",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\""));
        withGrant.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        withGrant.ErrorCode.ShouldBe("replay_release_not_allowed");

        var withoutGrant = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"release\":\"v1.0\""));
        withoutGrant.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        withoutGrant.ErrorCode.ShouldBe("replay_release_not_allowed");

        (await CountsAsync(world)).ShouldBe(before, "a refused replay writes no run, planned execution, slot, outbox, idempotency or audit row");
    }

    // ------------------------------------------------------------------ body shape (422 request_invalid, no write)

    [Fact]
    public async Task CreateRun_PurposeInvalid_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var before = await CountsAsync(world);

        // purpose: not one of the two values.
        var unknown = await RunCreationTests.CreateRunAsync(world, Body(world.ManifestId, ",\"purpose\":\"adhoc\""));
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknown.ErrorCode.ShouldBe("request_invalid");
        DetailPath(unknown).ShouldBe("/purpose");
        DetailReason(unknown).ShouldBe("invalid_value");

        // purpose: a JSON number, not a string.
        var number = await RunCreationTests.CreateRunAsync(world, Body(world.ManifestId, ",\"purpose\":7"));
        number.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        number.ErrorCode.ShouldBe("request_invalid");
        DetailPath(number).ShouldBe("/purpose");

        // purpose: an explicit null, which is present and still not a string.
        var nullPurpose = await RunCreationTests.CreateRunAsync(world, Body(world.ManifestId, ",\"purpose\":null"));
        nullPurpose.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        nullPurpose.ErrorCode.ShouldBe("request_invalid");
        DetailPath(nullPurpose).ShouldBe("/purpose");

        // replay_grant_id: not a uuid.
        var notAGuid = await RunCreationTests.CreateRunAsync(world, Body(world.ManifestId, ",\"replay_grant_id\":\"not-a-guid\""));
        notAGuid.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        notAGuid.ErrorCode.ShouldBe("request_invalid");
        DetailPath(notAGuid).ShouldBe("/replay_grant_id");
        DetailReason(notAGuid).ShouldBe("invalid_value");

        // replay_grant_id: a well-formed uuid, but a grant is meaningful only for purpose=replay.
        var regularWithGrant = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"regular\",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\""));
        regularWithGrant.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        regularWithGrant.ErrorCode.ShouldBe("request_invalid");
        DetailPath(regularWithGrant).ShouldBe("/replay_grant_id");

        var absentWithGrant = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\""));
        absentWithGrant.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        absentWithGrant.ErrorCode.ShouldBe("request_invalid");
        DetailPath(absentWithGrant).ShouldBe("/replay_grant_id");

        (await CountsAsync(world)).ShouldBe(before, "a refused body writes no run, planned execution, slot, outbox, idempotency or audit row");
    }

    // ------------------------------------------------------------------ the regular path is unchanged

    [Fact]
    public async Task CreateRun_PurposeRegular_ExplicitOrDefault_Creates201AndStoresRegular()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var explicitRegular = await RunCreationTests.CreateRunAsync(world, Body(world.ManifestId, ",\"purpose\":\"regular\""));
        explicitRegular.Status.ShouldBe(HttpStatusCode.Created, explicitRegular.ErrorMessage);
        explicitRegular.Text("state").ShouldBe("queued");

        var defaulted = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        defaulted.Status.ShouldBe(HttpStatusCode.Created, defaulted.ErrorMessage);

        (await _fixture.ColumnAsync<string>(
            world.Database, $"select purpose from public.run where org_id = '{world.Org:D}' order by created_at, id"))
            .ShouldBe(["regular", "regular"]);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where org_id = '{world.Org:D}' and purpose = 'regular' and replay_grant_id is null")).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where org_id = '{world.Org:D}' and replay_grant_id is not null")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ the refusal stores nothing (key stays free)

    [Fact]
    public async Task CreateRun_ReplayRefusal_DoesNotStoreIdempotencyRecord()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();

        var refused = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"" + Guid.CreateVersion7() + "\""),
            idempotencyKey: key);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("replay_grant_invalid");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.idempotency_record where key = '{key}'"))
            .ShouldBe(0, "a failure Result rolls the begin row back with the transaction");

        // The key is free again: a valid regular request with the same key but a different body is a create, not a
        // 409 idempotency_conflict.
        var retry = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId), idempotencyKey: key);

        retry.Status.ShouldBe(HttpStatusCode.Created, retry.ErrorMessage);
        (await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.run where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ a grant id from elsewhere is no grant

    [Fact]
    public async Task CreateRun_ReplayGrantOfAnotherProject_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var other = await RunCreationTests.StartAsync(_fixture, slug: "rival");

        // Forward-looking pin for 08.4.3: while every replay is refused (no grant store), both answers below are the
        // same 422 replay_grant_invalid, so this cannot fail yet — it pins the cross-project isolation for the day the
        // grant reader lands and a foreign id (which must not resolve) and an own id (which may) can be told apart.
        // Two run ids that really exist — one in another org's project, one in this very project.
        var otherRun = await RunCreationTests.CreateRunAsync(other, RunCreationTests.RunBody(other.ManifestId));
        otherRun.Status.ShouldBe(HttpStatusCode.Created, otherRun.ErrorMessage);
        var ownRun = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        ownRun.Status.ShouldBe(HttpStatusCode.Created, ownRun.ErrorMessage);

        var before = await CountsAsync(world);

        var foreignAnswer = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"" + otherRun.Id("run_id") + "\""));
        foreignAnswer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        foreignAnswer.ErrorCode.ShouldBe("replay_grant_invalid");

        var ownAnswer = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, ",\"purpose\":\"replay\",\"replay_grant_id\":\"" + ownRun.Id("run_id") + "\""));
        ownAnswer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        ownAnswer.ErrorCode.ShouldBe("replay_grant_invalid");

        (await CountsAsync(world)).ShouldBe(before, "a refused replay writes no run, planned execution, slot, outbox, idempotency or audit row");
    }

    // ------------------------------------------------------------------ run.purpose has no update path

    /// <summary>
    /// Migration 0031 grants the app role SELECT, INSERT only on <c>run</c>, so <c>purpose</c> (and
    /// <c>replay_grant_id</c>) is immutable once written: an UPDATE as the app role is refused with SQLSTATE 42501,
    /// exactly the path RunSchemaTests.AppRole_CannotUpdateOrDelete_Run asserts for <c>state</c>. 05.3.1 adds its own
    /// grant for <c>state</c> only and must not grant UPDATE on purpose/replay_grant_id.
    /// </summary>
    [Fact]
    public async Task CreateRun_Purpose_NoUpdatePath()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync("update public.run set purpose = 'replay'");

        error.SqlState.ShouldBe("42501");
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select purpose from public.run where org_id = '{world.Org:D}'")).ShouldBe("regular");
    }
}

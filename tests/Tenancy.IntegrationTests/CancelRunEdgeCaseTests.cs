// 05.4.1-T1 QA: the cancel body and the idempotency key at their edges — the code-point limit, the transport
// cap, malformed JSON, and the replay/conflict answers of one key.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class CancelRunEdgeCaseTests(PostgresTenancyFixture fixture)
{
    /// <summary>
    /// §5: the limit counts Unicode code points, the unit PostgreSQL's <c>char_length</c> and the contract's
    /// <c>maxLength</c> use, so 1000 astral characters (2000 UTF-16 units, 4000 UTF-8 bytes) are accepted and
    /// stored untrimmed.
    /// </summary>
    [Theory]
    [InlineData("r", 1000)]
    [InlineData("ệ", 1000)]
    [InlineData("🙂", 501)]
    [InlineData("🙂", 1000)]
    public async Task CancelRun_ReasonWithinCodePointLimit_AcceptedAndPreserved(string character, int count)
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var reason = string.Concat(Enumerable.Repeat(character, count));
        var body = $$"""{"reason":"{{reason}}"}""";
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(4096);

        var answer = await CancelRunTestData.CancelRawAsync(world.Client, runId, body, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.RowCountAsync(fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(fixture, world, runId)).Reason.ShouldBe(reason);
    }

    /// <summary>
    /// §5: 1001 code points are refused as <c>too_long</c> whatever their UTF-16 width, and the refusal records
    /// nothing; the 1000-character cancel that follows is the accepted control on the same run.
    /// </summary>
    [Fact]
    public async Task CancelRun_ReasonOverCodePointLimit_422TooLong()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var overLimit = string.Concat(Enumerable.Repeat("🙂", 1001));

        var refused = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, $$"""{"reason":"{{overLimit}}"}""", world.Admin);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        var detail = CancelRunTestData.Details(refused).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/reason");
        detail.GetProperty("reason").GetString().ShouldBe("too_long");
        (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(fixture, world, runId)).ShouldBe(0);

        var control = string.Concat(Enumerable.Repeat("🙂", 1000));
        (await CancelRunTestData.CancelRawAsync(
            world.Client, runId, $$"""{"reason":"{{control}}"}""", world.Admin))
            .Status.ShouldBe(HttpStatusCode.Accepted);
        (await CancelRunTestData.RowAsync(fixture, world, runId)).Reason.ShouldBe(control);
    }

    /// <summary>
    /// §5: every unusable body is a 422 <c>request_invalid</c> — blank text, NUL, a lone surrogate, a member of
    /// the wrong type, or a body that is not one JSON object — and it changes nothing; the accepted cancel at the
    /// end is the positive control.
    /// </summary>
    [Theory]
    [InlineData("""{"reason":""}""")]
    [InlineData("""{"reason":"\t\r\n"}""")]
    [InlineData("""{"reason":"\u00a0"}""")]
    [InlineData("""{"reason":"\u0000"}""")]
    [InlineData("""{"reason":"\ud800"}""")]
    [InlineData("""{"reason":42}""")]
    [InlineData("""{"reason":true}""")]
    [InlineData("""{"reason":[]}""")]
    [InlineData("""{"reason":{}}""")]
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("{} trailing")]
    public async Task CancelRun_InvalidReasonOrJson_422WithoutSideEffects(string body)
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var before = await SnapshotAsync(world, runId);

        var answer = await CancelRunTestData.CancelRawAsync(world.Client, runId, body, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await SnapshotAsync(world, runId)).ShouldBe(before);
        var control = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, """{"reason":"valid reason"}""", world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Accepted, control.ErrorMessage);
    }

    /// <summary>
    /// §5: the transport cap is exactly 4 KiB — a body that fills it is parsed and accepted, one more byte is a
    /// 413 <c>payload_too_large</c> that records nothing; the accepted cancel afterwards is the positive control.
    /// </summary>
    [Theory]
    [InlineData(4096, HttpStatusCode.Accepted)]
    [InlineData(4097, HttpStatusCode.RequestEntityTooLarge)]
    public async Task CancelRun_JsonAtTransportBoundary_EnforcesExactByteCap(int bytes, HttpStatusCode expected)
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var body = "{}" + new string(' ', bytes - 2);
        Encoding.UTF8.GetByteCount(body).ShouldBe(bytes);
        var before = await SnapshotAsync(world, runId);

        var answer = await CancelRunTestData.CancelRawAsync(world.Client, runId, body, world.Admin);

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        if (expected == HttpStatusCode.Accepted)
        {
            (await CancelRunTestData.RowCountAsync(fixture, world, runId)).ShouldBe(1);
            (await CancelRunTestData.StateAsync(fixture, world, runId)).ShouldBe("canceled");
        }
        else
        {
            answer.ErrorCode.ShouldBe("payload_too_large");
            (await SnapshotAsync(world, runId)).ShouldBe(before);
            (await CancelRunTestData.CancelRawAsync(world.Client, runId, "{}", world.Admin))
                .Status.ShouldBe(HttpStatusCode.Accepted);
        }
    }

    /// <summary>§8: a byte-identical retry under the same key replays the first <c>202</c> and changes nothing.</summary>
    [Fact]
    public async Task CancelRun_SameKeyAndBody_ReplaysWithoutSideEffects()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var key = NewKey();
        const string body = """{"reason":"stop"}""";
        var first = await CancelRunTestData.CancelRawAsync(world.Client, runId, body, world.Admin, key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, runId);

        var replay = await CancelRunTestData.CancelRawAsync(world.Client, runId, body, world.Admin, key);

        replay.Status.ShouldBe(first.Status, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await SnapshotAsync(world, runId)).ShouldBe(before);
        (await CancelRunTestData.RowCountAsync(fixture, world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// §8: the key is claimed for the canonical request, so the same request spelled with other JSON whitespace
    /// replays the first <c>202</c> instead of answering <c>idempotency_conflict</c> to a caller who repeated
    /// itself.
    /// </summary>
    [Fact]
    public async Task CancelRun_SameKeyAndEquivalentJson_ReplaysCanonicalRequest()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var key = NewKey();
        var first = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, """{"reason":"stop"}""", world.Admin, key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, runId);

        var replay = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, """{ "reason" : "stop" }""", world.Admin, key);

        replay.Status.ShouldBe(HttpStatusCode.Accepted, replay.ErrorMessage);
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        (await SnapshotAsync(world, runId)).ShouldBe(before);
    }

    /// <summary>§8: the same key with a different reason is a conflict, not a silent second request.</summary>
    [Fact]
    public async Task CancelRun_SameKeyAndDifferentReason_409WithoutChangingRequest()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var key = NewKey();
        var first = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, """{"reason":"first"}""", world.Admin, key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, runId);

        var conflict = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, """{"reason":"second"}""", world.Admin, key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await SnapshotAsync(world, runId)).ShouldBe(before);
    }

    /// <summary>§8: one key cannot cover two runs — the second run stays queued and its own cancel succeeds.</summary>
    [Fact]
    public async Task CancelRun_SameKeyAndDifferentRun_409LeavesSecondRunQueued()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var firstRun = await CancelRunTestData.CreateQueuedRunAsync(world);
        var secondRun = await CancelRunTestData.CreateQueuedRunAsync(world);
        var key = NewKey();
        var first = await CancelRunTestData.CancelRawAsync(world.Client, firstRun, "{}", world.Admin, key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var before = await SnapshotAsync(world, secondRun);

        var conflict = await CancelRunTestData.CancelRawAsync(world.Client, secondRun, "{}", world.Admin, key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await SnapshotAsync(world, secondRun)).ShouldBe(before);
        (await CancelRunTestData.StateAsync(fixture, world, secondRun)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(fixture, world, secondRun)).ShouldBe(0);
        (await CancelRunTestData.CancelAsync(world, secondRun, reason: null))
            .Status.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>
    /// §5.5 C2: a member with no role at all is authenticated but holds no permission, so the route refuses it
    /// with <c>403</c> and records nothing; the org admin's cancel afterwards is the positive control.
    /// </summary>
    [Fact]
    public async Task CancelRun_RolelessMember_403BeforeAnyStateChange()
    {
        await using var world = await RunCreationTests.StartAsync(fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var member = await AddMemberAsync(
            fixture, world.Client, world.Database, world.Org, world.Admin, roleCode: null);
        var before = await SnapshotAsync(world, runId);

        var denied = await CancelRunTestData.CancelAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(user: member, orgHeader: world.Org));

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        (await SnapshotAsync(world, runId)).ShouldBe(before);
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>The run, its cancel rows, outbox and audit rows as one text, so a refusal can compare it whole.</summary>
    private Task<string> SnapshotAsync(RunCreationTests.World world, Guid runId) =>
        fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select jsonb_build_object(
              'state', r.state,
              'version', r.version,
              'requests', (select count(*) from public.run_cancel_request c where c.org_id = r.org_id and c.run_id = r.id),
              'outbox', (select count(*) from public.outbox o where o.aggregate_id = r.id),
              'audit', (select count(*) from public.audit_log a where a.org_id = r.org_id and a.object_type = 'run' and a.object_id = '{runId}'))::text
              from public.run r where r.org_id = '{world.Org}' and r.id = '{runId}'
            """);
}

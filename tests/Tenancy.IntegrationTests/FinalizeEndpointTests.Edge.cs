using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FinalizeEndpointTests
{
    // ------------------------------------------------------------------ QA edge cases (independent pass on 05.3.2-T1)

    /// <summary>
    /// A finalize with a raw body and an optional key, black-box over HTTP. <paramref name="runSegment"/> is the route
    /// segment verbatim, so a non-GUID id can be sent. A body that is not JSON answers <c>{}</c> so the caller still
    /// sees the status.
    /// </summary>
    private static async Task<ApiAnswer> FinalizeRawAsync(
        HttpClient client,
        string runSegment,
        string rawBody,
        string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{runSegment}/finalize");
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        request.Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(IsJson(text) ? text : "{}");

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The valid body with one member changed (or removed), for a refusal that must leave no trace.</summary>
    private static async Task<Dictionary<string, object?>> MutatedBodyAsync(IngestTestWorld world, string mutation)
    {
        var body = await BodyAsync(world);
        var hash = (string)body["manifest_hash"]!;
        switch (mutation)
        {
            case "extra_member": body["extra"] = true; break;
            case "shard_string": body["shard"] = "1"; break;
            case "shard_zero": body["shard"] = 0; break;
            case "shard_overflow": body["shard"] = 2147483648L; break;
            case "last_sequence_negative": body["last_sequence"] = -1; break;
            case "count_negative": body["count"] = -1; break;
            case "producer_id_too_long": body["producer_id"] = new string('p', 300); break;
            case "producer_id_trailing_newline": body["producer_id"] = Producer + "\n"; break;
            case "shard_above_max": body["shard"] = 257; break;
            case "manifest_hash_uppercase": body["manifest_hash"] = hash.ToUpperInvariant(); break;
            case "manifest_hash_padded": body["manifest_hash"] = $" {hash} "; break;
            case "missing_count": body.Remove("count"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        return body;
    }

    /// <summary>The valid body padded with JSON whitespace to exactly <paramref name="bytes"/> UTF-8 bytes.</summary>
    private static async Task<string> PaddedBodyAsync(IngestTestWorld world, int bytes)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(await BodyAsync(world));
        var padded = json.Insert(json.Length - 1, new string(' ', bytes - json.Length));
        System.Text.Encoding.UTF8.GetByteCount(padded).ShouldBe(bytes);

        return padded;
    }

    /// <summary>A refusal leaves the slot open and undeclared, the run at its version, and writes no outbox row.</summary>
    private static async Task AssertNoEffectAsync(IngestTestWorld world, int runVersion)
    {
        (await SlotClosedAsync(world)).ShouldBeFalse();
        (await SlotDeclaresNothingAsync(world)).ShouldBeTrue();
        (await RunVersionAsync(world)).ShouldBe(runVersion);
        (await OutboxRowsAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData("extra_member")]
    [InlineData("shard_string")]
    [InlineData("shard_zero")]
    [InlineData("shard_overflow")]
    [InlineData("last_sequence_negative")]
    [InlineData("count_negative")]
    [InlineData("producer_id_too_long")]
    [InlineData("shard_above_max")]
    [InlineData("manifest_hash_uppercase")]
    [InlineData("manifest_hash_padded")]
    [InlineData("missing_count")]
    public async Task Finalize_MalformedMember_422RequestInvalid_NoEffect(string mutation)
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);
        var body = await MutatedBodyAsync(world, mutation);

        var answer = await FinalizeRawAsync(
            world.Client, world.RunId.ToString("D"), System.Text.Json.JsonSerializer.Serialize(body), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoEffectAsync(world, runVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public async Task Finalize_NonObjectBody_422RequestInvalid_NoEffect(string raw)
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(world.Client, world.RunId.ToString("D"), raw, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>
    /// A producer id with a trailing newline is a body error with its own pointer, refused by the parse. .NET's `$`
    /// also matches before a final newline, so a `$`-anchored shape would let it through and refuse it later as a
    /// generic registry 422 (or accept it where PostgreSQL's `~` would not): the shape is `\A…\z`, like
    /// <c>ProducerSlotArguments</c>' own rule.
    /// </summary>
    [Fact]
    public async Task Finalize_ProducerIdTrailingNewline_422OnProducerIdPointer_NoEffect()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(
            world.Client,
            world.RunId.ToString("D"),
            System.Text.Json.JsonSerializer.Serialize(await MutatedBodyAsync(world, "producer_id_trailing_newline")),
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        Details(answer).ShouldHaveSingleItem().GetProperty("path").GetString().ShouldBe("/producer_id");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>
    /// A shard above the contract's 256 is refused by the body parse on <c>/shard</c>, not left to the slot
    /// comparison: no run plans a slot that high, so the two answers are the same 422 but only this one is the
    /// contract's shape.
    /// </summary>
    [Fact]
    public async Task Finalize_ShardAboveMaximum_422OnShardPointer_NoEffect()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(
            world.Client,
            world.RunId.ToString("D"),
            System.Text.Json.JsonSerializer.Serialize(await MutatedBodyAsync(world, "shard_above_max")),
            NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        Details(answer).ShouldHaveSingleItem().GetProperty("path").GetString().ShouldBe("/shard");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>
    /// Text neither jsonb nor the canonical form can carry is a 422, never a 500 — in a member name (reading
    /// <c>member.Name</c> throws), in a member value, and in a duplicate member name (the duplicate check reads the
    /// names before any member loop runs). Each body is refused before a statement, so nothing changes.
    /// </summary>
    [Theory]
    [InlineData("{\"\\ud800\":1}")]
    [InlineData("{\"a\":\"\\ud800\"}")]
    [InlineData("{\"\\ud800\":1,\"\\ud800\":2}")]
    public async Task Finalize_UnstorableText_422RequestInvalid_NoEffect(string raw)
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(world.Client, world.RunId.ToString("D"), raw, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>A repeated member is ambiguous: the handler must refuse it, not pick one value silently.</summary>
    [Fact]
    public async Task Finalize_DuplicateMember_422RequestInvalid_NoEffect()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);
        var hash = await ManifestHashAsync(world);
        var raw = $"{{\"producer_id\":\"{Producer}\",\"shard\":1,\"last_sequence\":3,\"last_sequence\":9,\"count\":3,\"manifest_hash\":\"{hash}\"}}";

        var answer = await FinalizeRawAsync(world.Client, world.RunId.ToString("D"), raw, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>The 8 KiB cap is inclusive: exactly 8192 bytes is read and accepted.</summary>
    [Fact]
    public async Task Finalize_BodyAtEightKiB_202()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        await world.SetRunStateAsync("running");
        var raw = await PaddedBodyAsync(world, 8 * 1024);

        var answer = await FinalizeRawAsync(world.Client, world.RunId.ToString("D"), raw, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await RunStateAsync(world)).ShouldBe("collecting");
        (await SlotClosedAsync(world)).ShouldBeTrue();
    }

    /// <summary>One byte over the cap is refused with 413 before the body is parsed or any state is read.</summary>
    [Fact]
    public async Task Finalize_BodyOverEightKiB_413PayloadTooLarge_NoEffect()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);
        var raw = await PaddedBodyAsync(world, 8 * 1024 + 1);

        var answer = await FinalizeRawAsync(world.Client, world.RunId.ToString("D"), raw, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("payload_too_large");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>
    /// The contract marks <c>Idempotency-Key</c> required: a request without one is refused by the C2 idempotency
    /// stage with exactly 400 <c>request_invalid</c>, before the handler and the body parse.
    /// </summary>
    [Fact]
    public async Task Finalize_MissingIdempotencyKey_400RequestInvalid_NoEffect()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(
            world.Client, world.RunId.ToString("D"), System.Text.Json.JsonSerializer.Serialize(await BodyAsync(world)), null);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>The shared <c>IdempotencyKey</c> component caps the key at 128 characters.</summary>
    [Fact]
    public async Task Finalize_OverlongIdempotencyKey_400RequestInvalid_NoEffect()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(
            world.Client,
            world.RunId.ToString("D"),
            System.Text.Json.JsonSerializer.Serialize(await BodyAsync(world)),
            new string('k', 129));

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        await AssertNoEffectAsync(world, runVersion);
    }

    /// <summary>
    /// The stored 202 belongs to the credential that earned it: another identity replaying the same key with the
    /// byte-identical body is 409 <c>idempotency_conflict</c>, never the replay, so the T9 slot check cannot be
    /// bypassed through the idempotency record (the <c>CreateRunCommand</c> CiRunBinding precedent). The original
    /// credential's own retry still replays the stored 202.
    /// </summary>
    [Fact]
    public async Task Finalize_SameKeySameBodyOtherCredential_409IdempotencyConflict()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        await world.SetRunStateAsync("running");
        const string key = "finalize-qa-key-shared-across-identities";
        var body = await BodyAsync(world);

        var first = await FinalizeAsync(world.Client, world.RunId, body, key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);

        var replay = await FinalizeAsync(world.Client, world.RunId, body, key);
        replay.Status.ShouldBe(HttpStatusCode.Accepted, replay.ErrorMessage);

        // Another credential of the same project with the same key and body: a conflict, not somebody else's 202.
        await using var other = HostFor(world, IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti));
        using var otherClient = other.CreateClient();
        var foreign = await FinalizeAsync(otherClient, world.RunId, body, key);

        foreign.Status.ShouldBe(HttpStatusCode.Conflict, foreign.ErrorMessage);
        foreign.ErrorCode.ShouldBe("idempotency_conflict");
        (await SlotBoundJtiAsync(world)).ShouldBe(IngestTestWorld.Jti);
        (await SlotDeclaredLastSequenceAsync(world)).ShouldBe(3L);
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>Reusing a key for a different body is a conflict (Q5), not a second declaration.</summary>
    [Fact]
    public async Task Finalize_SameKeyDifferentBody_409IdempotencyConflict()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        const string key = "finalize-qa-same-key-diff-body";

        var first = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 3, count: 3), key);
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);

        var second = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 4, count: 4), key);

        second.Status.ShouldBe(HttpStatusCode.Conflict, second.ErrorMessage);
        second.ErrorCode.ShouldBe("idempotency_conflict");
        (await SlotDeclaredLastSequenceAsync(world)).ShouldBe(3L);
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>A refused request must not burn its key: the correct retry with the same key is accepted.</summary>
    [Fact]
    public async Task Finalize_RefusedWithKey_ThenCorrectWithSameKey_202()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        await world.SetRunStateAsync("running");
        const string key = "finalize-qa-refused-then-ok";

        var refused = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, shard: 2), key);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);

        var accepted = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world), key);

        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
        (await RunStateAsync(world)).ShouldBe("collecting");
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>Five repeats with a mix of reused and fresh keys: one transition, one audit row, one outbox row.</summary>
    [Fact]
    public async Task Finalize_FiveRepeats_MixedKeys_OneTransitionOneOutboxRow()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        await world.SetRunStateAsync("running");
        var body = await BodyAsync(world);

        var first = await FinalizeAsync(world.Client, world.RunId, body, "finalize-qa-repeat-shared");
        first.Status.ShouldBe(HttpStatusCode.Accepted, first.ErrorMessage);
        var versionAfterFirst = await RunVersionAsync(world);

        for (var i = 0; i < 4; i++)
        {
            var key = i % 2 == 0 ? "finalize-qa-repeat-shared" : NewKey();
            var repeat = await FinalizeAsync(world.Client, world.RunId, body, key);
            repeat.Status.ShouldBe(HttpStatusCode.Accepted, repeat.ErrorMessage);
        }

        (await RunVersionAsync(world)).ShouldBe(versionAfterFirst);
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);
        (await TransitionAuditRowsAsync(world)).ShouldBe(1);
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A refused first write must roll its binding back (§8): an unbound slot refused for shard mismatch stays
    /// unbound, so a different identity can still claim it.
    /// </summary>
    [Fact]
    public async Task Finalize_RefusedShardFirst_DoesNotBindSlot()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);

        var refused = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, shard: 2));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        (await SlotBoundJtiAsync(world)).ShouldBe(string.Empty);

        await using var other = HostFor(world, IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti));
        using var otherClient = other.CreateClient();
        var accepted = await FinalizeAsync(otherClient, world.RunId, await BodyAsync(world));

        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
        (await SlotBoundJtiAsync(world)).ShouldBe(IngestTestWorld.OtherJti);
    }

    /// <summary>The same rollback rule for a hash mismatch: an unbound slot refused on hash stays unbound.</summary>
    [Fact]
    public async Task Finalize_RefusedHashFirst_DoesNotBindSlot()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        var wrongHash = "sha256:" + new string('0', 64);

        var refused = await FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, manifestHash: wrongHash));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("manifest_invalid");
        (await SlotBoundJtiAsync(world)).ShouldBe(string.Empty);

        await using var other = HostFor(world, IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti));
        using var otherClient = other.CreateClient();
        var accepted = await FinalizeAsync(otherClient, world.RunId, await BodyAsync(world));

        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
        (await SlotBoundJtiAsync(world)).ShouldBe(IngestTestWorld.OtherJti);
    }

    /// <summary>Two identities race for one unbound slot: exactly one binds and closes, the other gets 403.</summary>
    [Fact]
    public async Task Finalize_ConcurrentForeignIdentities_OneWinsOtherRefused403()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await using var first = HostFor(world, IngestTestWorld.Writer());
        await using var second = HostFor(world, IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti));
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        var body = await BodyAsync(world);

        var answers = await Task.WhenAll(
            FinalizeAsync(firstClient, world.RunId, body),
            FinalizeAsync(secondClient, world.RunId, body));

        answers.Count(a => a.Status == HttpStatusCode.Accepted).ShouldBe(1);
        answers.Count(a => a.Status == HttpStatusCode.Forbidden && a.ErrorCode == "producer_slot_taken").ShouldBe(1);
        var winner = answers[0].Status == HttpStatusCode.Accepted ? IngestTestWorld.Jti : IngestTestWorld.OtherJti;
        (await SlotBoundJtiAsync(world)).ShouldBe(winner);
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>A run id that is not a GUID is a client error, never a server error.</summary>
    [Fact]
    public async Task Finalize_NonGuidRunId_ClientErrorNotServerError()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        var runVersion = await RunVersionAsync(world);

        var answer = await FinalizeRawAsync(
            world.Client, "not-a-guid", System.Text.Json.JsonSerializer.Serialize(await BodyAsync(world)), NewKey());

        ((int)answer.Status).ShouldBeInRange(400, 499);
        await AssertNoEffectAsync(world, runVersion);
    }
}

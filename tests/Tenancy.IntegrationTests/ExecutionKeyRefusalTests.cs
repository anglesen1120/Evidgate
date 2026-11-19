using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The ingest route refuses a well-formed execution key (64 lowercase hex) that was computed with the wrong byte
/// layout or from a wrong field: it is not any planned key, so membership validation answers
/// <c>unknown_execution_key</c> for exactly that event and the whole batch commits nothing. The canonical key is built
/// here from the documented layout, not by the production helper, so a layout regression in production turns the
/// positive control red instead of silently moving both sides together.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ExecutionKeyRefusalTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public ExecutionKeyRefusalTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    // Independent builder: sha256 over [4-byte big-endian length][UTF-8 bytes] for case_id ("D"), variant_hash, target_env.
    private static string Canonical(Guid caseId, string variantHash, string targetEnv)
    {
        using var stream = new MemoryStream();
        foreach (var field in new[] { caseId.ToString("D"), variantHash, targetEnv })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)bytes.Length);
            stream.Write(prefix);
            stream.Write(bytes);
        }

        return Hex(stream.ToArray());
    }

    private static string Hex(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Join(params string[] fields) => Hex(Encoding.UTF8.GetBytes(string.Concat(fields)));

    private static string LittleEndian(PlannedExecutionSeeder.SeededExecution e)
    {
        using var stream = new MemoryStream();
        foreach (var field in new[] { e.CaseId.ToString("D"), e.VariantHash, e.TargetEnv })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            stream.Write(prefix);
            stream.Write(bytes);
        }

        return Hex(stream.ToArray());
    }

    private static string FlipLastChar(string value) =>
        value[..^1] + (value[^1] == '0' ? '1' : '0');

    /// <summary>Wrong-layout candidates built from the first seeded execution's own fields.</summary>
    private static Dictionary<string, string> WrongLayouts(PlannedExecutionSeeder.SeededExecution e) => new()
    {
        ["no_length_prefixes"] = Join(e.CaseId.ToString("D"), e.VariantHash, e.TargetEnv),
        ["pipe_separator"] = Join(e.CaseId.ToString("D"), "|", e.VariantHash, "|", e.TargetEnv),
        ["little_endian_prefixes"] = LittleEndian(e),
        ["field_order_swapped"] = Canonical(e.CaseId, e.TargetEnv, e.VariantHash),
        ["case_id_without_hyphens"] = Hex(
            [.. Prefixed(e.CaseId.ToString("N")), .. Prefixed(e.VariantHash), .. Prefixed(e.TargetEnv)]),
    };

    /// <summary>Wrong-field candidates: the canonical layout over a tuple that is not planned.</summary>
    private static Dictionary<string, string> WrongFields(PlannedExecutionSeeder.SeededExecution e) => new()
    {
        ["changed_target_env"] = Canonical(e.CaseId, e.VariantHash, "production"),
        ["changed_variant_hash"] = Canonical(e.CaseId, FlipLastChar(e.VariantHash), e.TargetEnv),
        ["changed_case_id"] = Canonical(e.CaseRevisionId, e.VariantHash, e.TargetEnv),
        ["variant_without_scheme"] = Canonical(e.CaseId, e.VariantHash["sha256:".Length..], e.TargetEnv),
    };

    private static byte[] Prefixed(string field)
    {
        var bytes = Encoding.UTF8.GetBytes(field);
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, bytes.Length);
        bytes.CopyTo(result, 4);

        return result;
    }

    private static int[] BadIndices(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("index").GetInt32())];

    private static string Scope(IngestTestWorld world) => $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'";

    private static Task<long> EventRowsAsync(IngestTestWorld world) => world.CountAsync("event", Scope(world));

    private static Task<long> BatchRowsAsync(IngestTestWorld world) => world.CountAsync("event_batch", Scope(world));

    private static object Single(string key, long sequence) =>
        IngestEventBatchTests.Batch(IngestEventBatchTests.Event(Guid.CreateVersion7(), key, sequence));

    [Fact]
    public async Task independent_canonical_key_equals_planned_key_and_is_accepted()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 3, hosts: _hosts);

        foreach (var execution in world.Plan.Executions)
        {
            Canonical(execution.CaseId, execution.VariantHash, execution.TargetEnv).ShouldBe(execution.ExecutionKey);
        }

        var first = world.Plan.Executions[0];
        var answer = await IngestEventBatchTests.PostEventsAsync(
            world, Single(Canonical(first.CaseId, first.VariantHash, first.TargetEnv), sequence: 1));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task wrong_layout_key_is_well_formed_distinct_and_refused()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 3, hosts: _hosts);
        var planned = world.Plan.Executions.Select(e => e.ExecutionKey).ToHashSet(StringComparer.Ordinal);
        var sequence = 0L;

        foreach (var (name, key) in WrongLayouts(world.Plan.Executions[0]))
        {
            key.Length.ShouldBe(64, name);
            key.ShouldMatch("^[0-9a-f]{64}$", name);
            planned.ShouldNotContain(key, name);

            var answer = await IngestEventBatchTests.PostEventsAsync(world, Single(key, ++sequence));

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, name);
            answer.ErrorCode.ShouldBe("unknown_execution_key", name);
            BadIndices(answer).ShouldBe([0], name);
        }

        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task wrong_field_key_reaches_membership_and_is_refused_not_invalid()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 3, hosts: _hosts);
        var planned = world.Plan.Executions.Select(e => e.ExecutionKey).ToHashSet(StringComparer.Ordinal);
        var sequence = 0L;

        foreach (var (name, key) in WrongFields(world.Plan.Executions[0]))
        {
            key.ShouldMatch("^[0-9a-f]{64}$", name);
            planned.ShouldNotContain(key, name);

            var answer = await IngestEventBatchTests.PostEventsAsync(world, Single(key, ++sequence));

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, name);
            answer.ErrorCode.ShouldBe("unknown_execution_key", name);
        }

        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task mixed_batch_names_only_bad_indices_commits_nothing_and_corrected_retry_succeeds()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 5, hosts: _hosts);
        var e0 = world.Plan.Executions[0];
        var e1 = world.Plan.Executions[1];
        string[] good = [.. world.Plan.Executions.Select(e => Canonical(e.CaseId, e.VariantHash, e.TargetEnv))];
        var good0 = good[0];
        var good1 = good[2];
        var wrongLayout = WrongLayouts(e0)["no_length_prefixes"];
        var wrongField = WrongFields(e1)["changed_target_env"];
        var wrongLayout2 = WrongLayouts(e1)["little_endian_prefixes"];

        // Indices 1, 3 and 4 are bad; 0 and 2 are canonical and must not be named.
        object Mixed(string k0, string k1, string k2, string k3, string k4) => IngestEventBatchTests.Batch(
            IngestEventBatchTests.Event(Guid.CreateVersion7(), k0, 1),
            IngestEventBatchTests.Event(Guid.CreateVersion7(), k1, 2),
            IngestEventBatchTests.Event(Guid.CreateVersion7(), k2, 3),
            IngestEventBatchTests.Event(Guid.CreateVersion7(), k3, 4),
            IngestEventBatchTests.Event(Guid.CreateVersion7(), k4, 5));

        var refused = await IngestEventBatchTests.PostEventsAsync(
            world, Mixed(good0, wrongLayout, good1, wrongField, wrongLayout2));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("unknown_execution_key");
        BadIndices(refused).ShouldBe([1, 3, 4]);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);

        // Positive control: the same batch with the bad keys corrected commits whole.
        var retry = await IngestEventBatchTests.PostEventsAsync(world, Mixed(good[0], good[1], good[2], good[3], good[4]));

        retry.Status.ShouldBe(HttpStatusCode.OK, retry.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(5);
        (await BatchRowsAsync(world)).ShouldBe(1);
    }
}

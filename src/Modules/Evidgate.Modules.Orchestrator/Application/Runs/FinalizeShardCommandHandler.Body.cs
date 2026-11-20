using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

internal sealed partial class FinalizeShardCommandHandler
{
    // A repeated member name would make a declared value ambiguous, so it is refused with the rest of the invalid shapes.
    private static readonly JsonDocumentOptions DuplicateFreeOptions = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// The body's member set and the five shapes, in the order the contract lists them (00 §9.3, 05.3.2 §5). A
    /// member outside <see cref="Members"/> is 422 <c>additional_properties</c>, a missing or mistyped one 422
    /// <c>request_invalid</c> naming the pointer — exactly the shape the run-create body parse gives.
    /// </summary>
    private static Result<FinalizeShardRequest> ParseBody(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, DuplicateFreeOptions);
        }
        catch (JsonException)
        {
            return BodyInvalid("the request body is not JSON or repeats a member name", [new ErrorDetail(string.Empty, ApiErrorCode.RequestInvalid)]);
        }
        catch (InvalidOperationException)
        {
            // The duplicate check reads member names to compare them, and transcoding a lone-surrogate escape throws
            // instead of returning a JsonException. The body is unreadable either way, so it is the same 422.
            return BodyInvalid("the request body carries text that cannot be stored", [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        using var parsed = document;
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BodyInvalid("the request body must be a JSON object", [new ErrorDetail(string.Empty, ApiErrorCode.RequestInvalid)]);
        }

        // Before a single member is named: reading member.Name on a lone-surrogate name throws, so the body is refused
        // 422 here rather than escaping as a 500 (the ActivatePolicy precedent).
        if (UnstorableJsonText.HasUnstorableText(root))
        {
            // A lone surrogate or a NUL: neither the canonical form nor jsonb can carry it.
            return BodyInvalid("the request body carries text that cannot be stored", [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        foreach (var member in root.EnumerateObject())
        {
            if (!Members.Contains(member.Name, StringComparer.Ordinal))
            {
                return BodyInvalid("the request body carries an unknown field", [new ErrorDetail(RequestMemberPath.ForUnknown(member.Name), "additional_properties")]);
            }
        }

        if (!root.TryGetProperty("producer_id", out var producerElement))
        {
            return Required("producer_id");
        }

        if (producerElement.ValueKind != JsonValueKind.String || !ProducerIdShape.IsMatch(producerElement.GetString()!))
        {
            return BodyInvalid(
                $"producer_id must match {ProducerSlotLimits.ProducerIdPattern}",
                [new ErrorDetail("/producer_id", InvalidValue)]);
        }

        if (!root.TryGetProperty("shard", out var shardElement))
        {
            return Required("shard");
        }

        // The contract's bound is enforced here, not left to the slot comparison (a shard above 256 names a slot no
        // run can plan): 422 /shard either way, but with the shape the contract declares.
        if (shardElement.ValueKind != JsonValueKind.Number
            || !shardElement.TryGetInt32(out var shard)
            || shard < 1
            || shard > MaxShard)
        {
            return BodyInvalid($"shard must be an integer between 1 and {MaxShard}", [new ErrorDetail("/shard", InvalidValue)]);
        }

        if (!root.TryGetProperty("last_sequence", out var sequenceElement))
        {
            return Required("last_sequence");
        }

        if (sequenceElement.ValueKind != JsonValueKind.Number
            || !sequenceElement.TryGetInt64(out var lastSequence)
            || lastSequence < 0)
        {
            return BodyInvalid(
                "last_sequence must be an integer of at least 0",
                [new ErrorDetail("/last_sequence", InvalidValue)]);
        }

        if (!root.TryGetProperty("count", out var countElement))
        {
            return Required("count");
        }

        // int, not long: producer_slot.declared_count is an integer column (migration 0035), so a value that would
        // overflow it is refused here rather than surfacing as an unhandled 22003 from the cast.
        if (countElement.ValueKind != JsonValueKind.Number
            || !countElement.TryGetInt32(out var count)
            || count < 0)
        {
            return BodyInvalid(
                "count must be an integer of at least 0",
                [new ErrorDetail("/count", InvalidValue)]);
        }

        if (!root.TryGetProperty("manifest_hash", out var hashElement))
        {
            return Required("manifest_hash");
        }

        if (hashElement.ValueKind != JsonValueKind.String || !ManifestHashShape().IsMatch(hashElement.GetString()!))
        {
            return BodyInvalid(
                "manifest_hash must match ^sha256:[0-9a-f]{64}$",
                [new ErrorDetail("/manifest_hash", InvalidValue)]);
        }

        return Result.Success(new FinalizeShardRequest(
            producerElement.GetString()!,
            shard,
            lastSequence,
            count,
            hashElement.GetString()!));
    }

    /// <summary>The one member rule every Orchestrator body parse shares: a missing member is 422 naming its pointer.</summary>
    private static Error Invalid(string name, string reason, string message) =>
        Error.Invalid(message, [new ErrorDetail($"/{name}", reason)]);

    /// <summary>422 <c>request_invalid</c> as a failed result, with the parse's own details.</summary>
    private static Result<FinalizeShardRequest> BodyInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Result.Failure<FinalizeShardRequest>(Error.Invalid(message, details));

    /// <summary>422 <c>request_invalid</c> for an absent required member, naming its pointer (the run-create shape).</summary>
    private static Result<FinalizeShardRequest> Required(string name) =>
        Result.Failure<FinalizeShardRequest>(Invalid(name, "required", $"{name} is required"));

    /// <summary>The contract's <c>shard</c> maximum (05.3.2 §5, 256 planned executions per run at most).</summary>
    private const int MaxShard = 256;

    /// <summary>
    /// The 00 §7 producer-id shape the slot table's CHECK mirrors (04.1.2's <c>ProducerSlotLimits</c>): the shared
    /// constant with its trailing <c>$</c> replaced by <c>\z</c>, because .NET's <c>$</c> also matches before a
    /// trailing newline while PostgreSQL's <c>~</c> does not (<c>ProducerSlotArguments</c>' own construction). Built at
    /// runtime for that reason, so the shared constant stays the one source of the charset.
    /// </summary>
    private static readonly Regex ProducerIdShape = new(
        ProducerSlotLimits.ProducerIdPattern[..^1] + @"\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>The contract's <c>manifest_hash</c> pattern (00 §9.3, 05.3.2 §5): <c>sha256:</c> + 64 lower-case hex.</summary>
    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestHashShape();

    /// <summary>One validated finalize body (00 §9.3).</summary>
    /// <param name="ProducerId">The manifest shard's deterministic producer id (T54).</param>
    /// <param name="Shard">The 1-based manifest shard index; it must be the bound slot's own (Q2).</param>
    /// <param name="LastSequence">The highest producer sequence the shard declares it sent.</param>
    /// <param name="Count">How many events the shard declares it sent.</param>
    /// <param name="ManifestHash">The run's manifest hash, as the shard knows it (AC3).</param>
    private sealed record FinalizeShardRequest(
        string ProducerId,
        int Shard,
        long LastSequence,
        int Count,
        string ManifestHash);
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Modules.Ingestion.Domain.Events;

/// <summary>
/// The pure envelope parser of 04.1.1-T1: the batch body of <c>POST /v1/runs/{r}/events</c>
/// (<c>{"events": [ … ]}</c>, 00 §9.4) into <see cref="ParsedEvent"/> records plus the per-event
/// <see cref="ErrorDetail"/>s that reject the whole batch. No I/O, no clock, no DI, and total over
/// hostile input: the body is already bounded by the route's 1 MiB cap (00 §5.5 P8, P8/D-A8), a
/// <see cref="JsonException"/> is an error rather than a throw, and every failure is reported with the
/// 00 §9.2 <c>index</c> and <c>path</c> of the offending member.
/// </summary>
/// <remarks>
/// The Corvus edge validation (00 §5.5 Edge) does <b>not</b> run on this route, so this parser is the
/// only gate: it re-checks the 00 §9.4 required members, the closed <c>type</c> set, the schema's value
/// bounds and the schema's <c>additionalProperties: false</c>. A member the envelope schema does not
/// name (the legacy <c>kind</c>, a typo) is refused as <c>unknown_member</c> — at the batch level
/// (<c>/&lt;name&gt;</c>) and at the event level (<c>/events/&lt;i&gt;/&lt;name&gt;</c>) — so it is
/// never hashed into <c>payload_hash</c> and never silently kept; <c>ext</c> stays open, as the schema
/// leaves it. Only the first unnamed member of each level is reported: the batch is refused whole, so
/// one pointer is enough and the error body stays bounded. When
/// <see cref="EventBatchParseResult.Errors"/> is non-empty the batch is rejected whole (422
/// <c>event_invalid</c>) and <see cref="EventBatchParseResult.Events"/> is empty, because a batch commits
/// all of its events or none (AC1/AC3).
/// </remarks>
internal static partial class EventBatchParser
{
    /// <summary>The batch cap of the envelope schema and of AC7 (<c>maxItems</c> 500).</summary>
    internal const int MaxEvents = 500;

    internal const string TestStarted = "test.started";
    internal const string TestStep = "test.step";
    internal const string TestFinished = "test.finished";
    internal const string RunLog = "run.log";
    internal const string ProducerHeartbeat = "producer.heartbeat";

    /// <summary>The closed <c>type</c> set of 00 §9.4 (D1: enforced here, not by editing the shared schema).</summary>
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        TestStarted,
        TestStep,
        TestFinished,
        RunLog,
        ProducerHeartbeat,
    };

    /// <summary>
    /// The closed member set of <c>$defs/batch</c> (contracts/schemas/event-envelope.v1.json,
    /// <c>additionalProperties: false</c>). The route carries no Corvus edge, so this parser is the only
    /// place the closed batch envelope is enforced.
    /// </summary>
    private static readonly HashSet<string> BatchMembers = new(StringComparer.Ordinal)
    {
        "schema_version",
        "events",
    };

    /// <summary>
    /// The closed member set of <c>$defs/event</c> (contracts/schemas/event-envelope.v1.json,
    /// <c>additionalProperties: false</c>). <c>ext</c> is a member here, but the schema leaves the
    /// <c>ext</c> object itself open, so its keys are not checked.
    /// </summary>
    private static readonly HashSet<string> EventMembers = new(StringComparer.Ordinal)
    {
        "schema_version",
        "event_id",
        "producer_id",
        "sequence",
        "job_id",
        "epoch",
        "execution_key",
        "attempt_no",
        "type",
        "status",
        "duration_ms",
        "occurred_at",
        "artifact_ids",
        "ext",
    };

    /// <summary>
    /// Parses one batch body. The result carries either the accepted events (in request order) or the
    /// errors that reject the batch; it never throws on hostile input. The result owns a clone of every
    /// accepted element, so the parse document is disposed before this method returns.
    /// </summary>
    /// <param name="body">The raw request body (≤ 1 MiB before decompression, enforced at the transport layer).</param>
    public static EventBatchParseResult Parse(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    // The same depth cap the canonicalizer uses, so a body the canonicalizer would refuse
                    // is refused here first (00 §5.7 S5).
                    MaxDepth = JsonCanonicalizer.MaxDepth,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });
        }
        catch (JsonException)
        {
            // Not JSON, deeper than the cap, or more than one document.
            return EventBatchParseResult.Rejected([new ErrorDetail(string.Empty, "malformed")]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                // The route takes the batch envelope only: a bare single event is not a batch.
                return EventBatchParseResult.Rejected([new ErrorDetail(string.Empty, "invalid_type")]);
            }

            var errors = new List<ErrorDetail>();

            if (root.TryGetProperty("schema_version", out var batchVersion)
                && (batchVersion.ValueKind != JsonValueKind.Number
                    || !batchVersion.TryGetInt64(out var version)
                    || version != 1))
            {
                errors.Add(new ErrorDetail("/schema_version", "invalid_value"));
            }

            if (!root.TryGetProperty("events", out var events))
            {
                errors.Add(new ErrorDetail("/events", "required"));
                return EventBatchParseResult.Rejected(errors);
            }

            if (events.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ErrorDetail("/events", "invalid_type"));
                return EventBatchParseResult.Rejected(errors);
            }

            var count = events.GetArrayLength();
            if (count == 0)
            {
                errors.Add(new ErrorDetail("/events", "empty"));
                return EventBatchParseResult.Rejected(errors);
            }

            if (count > MaxEvents)
            {
                // AC7: over the cap is a 413 batch_too_large; the reason lets the handler map it without
                // re-counting, and nothing is parsed.
                errors.Add(new ErrorDetail("/events", "too_large"));
                return EventBatchParseResult.Rejected(errors);
            }

            // additionalProperties: false of $defs/batch: the batch envelope names only schema_version and
            // events, and this route has no Corvus edge to enforce it, so an unnamed member (the legacy
            // `kind`, a typo) is refused here instead of being accepted and carried. The first one is
            // reported and the batch is refused whole, like every other batch-level failure.
            foreach (var member in root.EnumerateObject())
            {
                if (!BatchMembers.Contains(member.Name))
                {
                    errors.Add(new ErrorDetail("/" + member.Name, "unknown_member"));
                    return EventBatchParseResult.Rejected(errors);
                }
            }

            var parsed = new List<ParsedEvent>(count);
            string? batchProducer = null;
            var producerMismatchReported = false;

            for (var index = 0; index < count; index++)
            {
                var parsedEvent = ParseEvent(index, events[index], errors);
                if (parsedEvent is null)
                {
                    continue;
                }

                if (batchProducer is null)
                {
                    batchProducer = parsedEvent.ProducerId;
                }
                else if (!producerMismatchReported
                    && !string.Equals(batchProducer, parsedEvent.ProducerId, StringComparison.Ordinal))
                {
                    // D4: one batch carries one producer; the first deviating index rejects the batch.
                    errors.Add(new ErrorDetail(
                        "/events/" + index.ToString(CultureInfo.InvariantCulture) + "/producer_id",
                        "producer_mismatch",
                        index));
                    producerMismatchReported = true;
                }

                parsed.Add(parsedEvent);
            }

            return errors.Count == 0
                ? EventBatchParseResult.Accepted(parsed)
                : EventBatchParseResult.Rejected(errors);
        }
    }

    /// <summary>Reads a required string member, or reports <c>required</c>/<c>invalid_type</c>.</summary>
    private static bool ReadString(
        JsonElement element,
        string name,
        string path,
        int index,
        List<ErrorDetail> errors,
        out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(name, out var member))
        {
            errors.Add(new ErrorDetail(path + "/" + name, "required", index));
            return false;
        }

        if (member.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ErrorDetail(path + "/" + name, "invalid_type", index));
            return false;
        }

        value = member.GetString()!;
        return true;
    }

    /// <summary>Reads a required integer member at or above <paramref name="minimum"/>.</summary>
    private static bool ReadInteger(
        JsonElement element,
        string name,
        string path,
        int index,
        List<ErrorDetail> errors,
        long minimum,
        out long value)
    {
        value = 0;

        if (!element.TryGetProperty(name, out var member))
        {
            errors.Add(new ErrorDetail(path + "/" + name, "required", index));
            return false;
        }

        return ReadMember(member, name, path, index, errors, minimum, out value);
    }

    /// <summary>Reads an optional integer member; absent is a null value, not an error.</summary>
    private static bool TryReadInteger(
        JsonElement element,
        string name,
        string path,
        int index,
        List<ErrorDetail> errors,
        long minimum,
        out long? value)
    {
        value = null;

        if (!element.TryGetProperty(name, out var member))
        {
            return true;
        }

        if (!ReadMember(member, name, path, index, errors, minimum, out var read))
        {
            return false;
        }

        value = read;
        return true;
    }

    /// <summary>Validates one integer member against the schema's type, integral value and minimum.</summary>
    private static bool ReadMember(
        JsonElement member,
        string name,
        string path,
        int index,
        List<ErrorDetail> errors,
        long minimum,
        out long value)
    {
        value = 0;

        if (member.ValueKind != JsonValueKind.Number)
        {
            errors.Add(new ErrorDetail(path + "/" + name, "invalid_type", index));
            return false;
        }

        if (!TryLong(member, out value))
        {
            errors.Add(new ErrorDetail(path + "/" + name, "out_of_range", index));
            return false;
        }

        if (value < minimum)
        {
            errors.Add(new ErrorDetail(path + "/" + name, "out_of_range", index));
            return false;
        }

        return true;
    }

    /// <summary>
    /// The schema says <c>integer</c>, so a whole number written with a fraction or an exponent
    /// (<c>1.0</c>, <c>1e2</c>) is accepted while a fractional or out-of-range value is not.
    /// </summary>
    private static bool TryLong(JsonElement member, out long value)
    {
        if (member.TryGetInt64(out value))
        {
            return true;
        }

        if (member.TryGetDouble(out var number)
            && double.IsFinite(number)
            && Math.Truncate(number) == number
            && number >= long.MinValue
            && number < 9223372036854775808.0)
        {
            value = (long)number;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>The <c>execution_key</c> shape of 00 §7: 64 lowercase hex characters.</summary>
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ExecutionKeyShape();

    /// <summary>RFC 3339 date-time: a full date, a time and a <c>Z</c> or numeric offset.</summary>
    [GeneratedRegex(
        @"\A\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:[Zz]|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Rfc3339();
}

/// <summary>
/// The outcome of <see cref="EventBatchParser.Parse"/>: the accepted events in request order, or the
/// errors that reject the batch whole. Exactly one of the two lists is non-empty.
/// </summary>
/// <param name="Events">The accepted envelopes, in request order; empty when <paramref name="Errors"/> is non-empty.</param>
/// <param name="Errors">The 422 <c>event_invalid</c> details; empty when the batch is accepted.</param>
internal sealed record EventBatchParseResult(
    IReadOnlyList<ParsedEvent> Events,
    IReadOnlyList<ErrorDetail> Errors)
{
    /// <summary>True when the batch parsed cleanly and may be committed.</summary>
    internal bool IsAccepted => Errors.Count == 0;

    /// <summary>The whole batch parsed: every event may be inserted.</summary>
    internal static EventBatchParseResult Accepted(IReadOnlyList<ParsedEvent> events) => new(events, []);

    /// <summary>The batch is refused: nothing may be inserted.</summary>
    internal static EventBatchParseResult Rejected(IReadOnlyList<ErrorDetail> errors) => new([], errors);
}

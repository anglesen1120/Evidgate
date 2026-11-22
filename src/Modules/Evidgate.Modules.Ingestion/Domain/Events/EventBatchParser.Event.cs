using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Modules.Ingestion.Domain.Events;

internal static partial class EventBatchParser
{
    /// <summary>
    /// Parses one envelope element. The first failure of the element is reported and the element is
    /// abandoned: the batch is rejected whole, so a partially valid element has no use.
    /// </summary>
    private static ParsedEvent? ParseEvent(int index, JsonElement element, List<ErrorDetail> errors)
    {
        var path = "/events/" + index.ToString(CultureInfo.InvariantCulture);

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ErrorDetail(path, "invalid_type", index));
            return null;
        }

        // additionalProperties: false of $defs/event: a member the envelope schema does not name (the
        // legacy `kind`, a typo) is refused before it can reach payload_hash — the hash is taken over the
        // element as received, so an unnamed member would otherwise be folded into the content address.
        // `ext` stays open: the schema does not restrict its keys, so they are not checked here.
        foreach (var member in element.EnumerateObject())
        {
            if (!EventMembers.Contains(member.Name))
            {
                errors.Add(new ErrorDetail(path + "/" + member.Name, "unknown_member", index));
                return null;
            }
        }

        // U+0000 and an unpaired surrogate reach neither jsonb nor the RFC 8785 form (04.7.1-T2.2/T2.3
        // guard); the walk also converts a getter that would throw into this 422.
        if (UnstorableText.Contains(element))
        {
            errors.Add(new ErrorDetail(path, "invalid_value", index));
            return null;
        }

        // schema_version — required, const 1.
        if (!ReadInteger(element, "schema_version", path, index, errors, 1, out var schemaVersion))
        {
            return null;
        }

        if (schemaVersion != 1)
        {
            errors.Add(new ErrorDetail(path + "/schema_version", "invalid_value", index));
            return null;
        }

        // event_id — required uuid, never the nil uuid (it is the dedupe identity).
        if (!ReadString(element, "event_id", path, index, errors, out var eventIdText))
        {
            return null;
        }

        if (!Guid.TryParse(eventIdText, out var eventId) || eventId == Guid.Empty)
        {
            errors.Add(new ErrorDetail(path + "/event_id", "invalid_format", index));
            return null;
        }

        // producer_id — required string, maxLength 128.
        if (!ReadString(element, "producer_id", path, index, errors, out var producerId))
        {
            return null;
        }

        if (producerId.Length > 128)
        {
            errors.Add(new ErrorDetail(path + "/producer_id", "too_long", index));
            return null;
        }

        // sequence — required integer >= 0 that fits a long.
        if (!ReadInteger(element, "sequence", path, index, errors, 0, out var sequence))
        {
            return null;
        }

        // job_id — optional uuid or null.
        Guid? jobId = null;
        if (element.TryGetProperty("job_id", out var jobIdElement)
            && jobIdElement.ValueKind != JsonValueKind.Null)
        {
            if (jobIdElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(jobIdElement.GetString(), out var parsedJobId))
            {
                errors.Add(new ErrorDetail(path + "/job_id", "invalid_format", index));
                return null;
            }

            jobId = parsedJobId;
        }

        // epoch — optional integer >= 0.
        if (!TryReadInteger(element, "epoch", path, index, errors, 0, out var epoch))
        {
            return null;
        }

        // execution_key — required 64 lowercase hex characters.
        if (!ReadString(element, "execution_key", path, index, errors, out var executionKey))
        {
            return null;
        }

        if (!ExecutionKeyShape().IsMatch(executionKey))
        {
            errors.Add(new ErrorDetail(path + "/execution_key", "invalid_format", index));
            return null;
        }

        // attempt_no — required integer >= 1 that fits an int (the attempt writer's own cap is 05.3.3's).
        if (!ReadInteger(element, "attempt_no", path, index, errors, 1, out var attemptNo))
        {
            return null;
        }

        if (attemptNo > int.MaxValue)
        {
            errors.Add(new ErrorDetail(path + "/attempt_no", "out_of_range", index));
            return null;
        }

        // type — required member of the closed 00 §9.4 set.
        if (!ReadString(element, "type", path, index, errors, out var type))
        {
            return null;
        }

        if (!EventTypes.Contains(type))
        {
            errors.Add(new ErrorDetail(path + "/type", "event_type_unknown", index));
            return null;
        }

        // status — optional; the raw producer status, required when the type is test.finished.
        string? status = null;
        if (element.TryGetProperty("status", out var statusElement))
        {
            if (statusElement.ValueKind != JsonValueKind.String)
            {
                errors.Add(new ErrorDetail(path + "/status", "invalid_type", index));
                return null;
            }

            status = statusElement.GetString()!;
            if (status.Length > 64)
            {
                errors.Add(new ErrorDetail(path + "/status", "too_long", index));
                return null;
            }
        }

        if (status is null && string.Equals(type, TestFinished, StringComparison.Ordinal))
        {
            errors.Add(new ErrorDetail(path + "/status", "required", index));
            return null;
        }

        // duration_ms — optional integer >= 0.
        if (!TryReadInteger(element, "duration_ms", path, index, errors, 0, out var durationMs))
        {
            return null;
        }

        // occurred_at — required RFC 3339 date-time.
        if (!ReadString(element, "occurred_at", path, index, errors, out var occurredAtText))
        {
            return null;
        }

        if (!Rfc3339().IsMatch(occurredAtText)
            || !DateTimeOffset.TryParse(
                occurredAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var occurredAt))
        {
            errors.Add(new ErrorDetail(path + "/occurred_at", "invalid_format", index));
            return null;
        }

        // artifact_ids — optional array of uuid.
        var artifactIds = new List<Guid>();
        if (element.TryGetProperty("artifact_ids", out var artifactElement))
        {
            if (artifactElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ErrorDetail(path + "/artifact_ids", "invalid_type", index));
                return null;
            }

            var position = 0;
            foreach (var item in artifactElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(item.GetString(), out var artifactId))
                {
                    errors.Add(new ErrorDetail(
                        path + "/artifact_ids/" + position.ToString(CultureInfo.InvariantCulture),
                        "invalid_format",
                        index));
                    return null;
                }

                artifactIds.Add(artifactId);
                position++;
            }
        }

        // ext — optional object whose `schema` is required (contracts/schemas/event-envelope.v1.json).
        var hasExt = false;
        var ext = default(JsonElement);
        if (element.TryGetProperty("ext", out var extElement))
        {
            if (extElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ErrorDetail(path + "/ext", "invalid_type", index));
                return null;
            }

            if (!extElement.TryGetProperty("schema", out var schemaElement))
            {
                errors.Add(new ErrorDetail(path + "/ext/schema", "required", index));
                return null;
            }

            if (schemaElement.ValueKind != JsonValueKind.String)
            {
                errors.Add(new ErrorDetail(path + "/ext/schema", "invalid_type", index));
                return null;
            }

            if (schemaElement.GetString()!.Length > 128)
            {
                errors.Add(new ErrorDetail(path + "/ext/schema", "too_long", index));
                return null;
            }

            hasExt = true;
            ext = extElement;
        }

        // payload_hash — the RFC 8785 canonical form of the event object exactly as received (00 §7 and
        // the task file §4 DDL comment "sha256:<hex> over RFC 8785 canonical event"). A document the
        // canonicalizer refuses (duplicate names, a number outside the double range) is an error.
        string payloadHash;
        try
        {
            payloadHash = CanonicalHash.Compute(element);
        }
        catch (JsonCanonicalizationException)
        {
            errors.Add(new ErrorDetail(path, "not_canonicalisable", index));
            return null;
        }
        catch (JsonException)
        {
            errors.Add(new ErrorDetail(path, "not_canonicalisable", index));
            return null;
        }

        // payload — the ext allow-list residue (04 §6), bounded at 8 KiB (04 §8 S5).
        var residue = hasExt ? EventExtAllowList.Build(type, ext) : EventExtAllowList.Empty;
        if (residue.ByteCount > EventExtAllowList.MaxResidueBytes)
        {
            errors.Add(new ErrorDetail(path + "/ext", "ext_too_large", index));
            return null;
        }

        // Xfail is a test.finished fact only (04.3.2-T2; 05.3.3 consumes it as producer-asserted).
        var xfail = string.Equals(type, TestFinished, StringComparison.Ordinal)
            && hasExt
            && ext.TryGetProperty("xfail", out var xfailElement)
            && xfailElement.ValueKind == JsonValueKind.True;

        return new ParsedEvent(
            index,
            eventId,
            producerId,
            sequence,
            jobId,
            epoch,
            executionKey,
            (int)attemptNo,
            type,
            status,
            durationMs,
            occurredAt,
            artifactIds,
            payloadHash,
            residue.Json,
            xfail,
            element.Clone());
    }
}

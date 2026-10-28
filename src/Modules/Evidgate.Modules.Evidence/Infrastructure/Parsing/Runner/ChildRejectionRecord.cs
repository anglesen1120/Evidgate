using System.Text.Json;
using Evidgate.Modules.Evidence.Domain;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// Reads the child's own refusal out of a successful run's record stream (06.3.6 T1.4, D-S125-Q4): the one
/// <c>rejected</c> record whose <c>reason</c> becomes the artifact's tenant-visible failure code.
/// <para>
/// It lives under the profile's directory because it is the one in-process read of the child's bytes: 06.3.1 §11
/// (00 §5.7 S5) admits JSON parsing inside <c>Infrastructure/Parsing/</c> only, so no application-layer handler may
/// build the reader itself. The record stream is framed by <see cref="ChildOutputProtocol"/>; this type only decodes
/// the top level of a record the framing already bounded.
/// </para>
/// </summary>
internal static class ChildRejectionRecord
{
    /// <summary>The record kind a rejection carries, as the child's protocol spells it (<c>NdjsonWriter.RejectedKind</c>).</summary>
    private const string RejectedRecordKind = "rejected";

    /// <summary>The property a record's kind travels in (<c>NdjsonWriter.KindProperty</c>).</summary>
    private const string RecordKindProperty = "t";

    /// <summary>The property a rejection record's reason word travels in (<c>NdjsonWriter.ReasonProperty</c>).</summary>
    private const string RecordReasonProperty = "reason";

    /// <summary>
    /// The bytes the JSON value <c>rejected</c> always occupies in a record — quotes included, because that value has no
    /// escapable character — so a record can be dismissed as "not a rejection" without being parsed.
    /// </summary>
    private static ReadOnlySpan<byte> RejectedRecordKindBytes => "\"rejected\""u8;

    /// <summary>
    /// The failure code a successful stream's own <c>rejected</c> record carries, or <see langword="null"/> when the
    /// child reported no rejection (the stream is then the parsed records alone, and nothing is written).
    /// <para>
    /// The child writes one <c>rejected</c> record and still exits 0 with its marker when it refuses the content
    /// itself — a DTD, an unsafe archive entry name, an input over the cap — because the run succeeded and the
    /// document is what failed (D-S125-Q4). Only that record's <c>reason</c> is read, and only a value shaped like
    /// a code word (<c>^[a-z0-9_]{1,64}$</c>) is kept: anything else becomes
    /// <see cref="ArtifactFailureCode.ParseCrashed"/>, so no text the untrusted document influenced can reach the
    /// tenant-visible column (00 §5.7 S3).
    /// </para>
    /// </summary>
    /// <param name="succeeded">The run's records, in stream order.</param>
    /// <returns>The code to write, or <see langword="null"/> for a stream without a rejection record.</returns>
    public static string? ReasonIn(ParseRunSucceeded succeeded)
    {
        foreach (var record in succeeded.Records)
        {
            // A record that does not carry the literal `"rejected"` anywhere cannot be a rejection — no encoding of
            // that value escapes it — so the overwhelming majority of a stream's records are dismissed by one
            // vectorised search over bytes, without a reader, a string or a copy.
            if (record.Utf8Json.Span.IndexOf(RejectedRecordKindBytes) < 0)
            {
                continue;
            }

            if (TryReadRejectedReason(record.Utf8Json, out var reason))
            {
                return IsCodeWord(reason) ? reason : ArtifactFailureCode.ParseCrashed;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one record's top level only — its kind and, for a rejection, its reason — with
    /// <see cref="Utf8JsonReader"/> over the UTF-8 the child wrote. Nothing else of the record is decoded, kept or
    /// logged, so a hostile record costs no string for its content.
    /// </summary>
    /// <param name="utf8">The record's bytes, without the terminating newline.</param>
    /// <param name="reason">The rejection's <c>reason</c> string, or <see langword="null"/> when it is absent or not a string.</param>
    /// <returns><see langword="true"/> when the record is object-kind <c>rejected</c>.</returns>
    private static bool TryReadRejectedReason(ReadOnlyMemory<byte> utf8, out string? reason)
    {
        reason = null;

        if (utf8.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8.Span);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            string? kind = null;
            var kindRead = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                if (reader.ValueTextEquals(RecordKindProperty))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return false;
                    }

                    kind = reader.GetString();
                    kindRead = true;
                }
                else if (reader.ValueTextEquals(RecordReasonProperty))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reason = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        // A reason that is not a string is never a code word; the value is skipped, not walked, so a
                        // hostile record cannot make this reader traverse it.
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (kindRead && string.Equals(kind, RejectedRecordKind, StringComparison.Ordinal))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // A line the runner framed but that is not one JSON object is not a rejection this reader can read; the
            // framing check already reports such a stream as malformed output, and no verdict is invented from it.
        }

        reason = null;
        return false;
    }

    /// <summary>True when <paramref name="value"/> matches <c>^[a-z0-9_]{1,64}$</c> — the shape of a reason word.</summary>
    /// <param name="value">The child-supplied reason.</param>
    /// <returns><see langword="true"/> when the value may become a tenant-visible failure code.</returns>
    private static bool IsCodeWord(string? value)
    {
        if (value is null || value.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

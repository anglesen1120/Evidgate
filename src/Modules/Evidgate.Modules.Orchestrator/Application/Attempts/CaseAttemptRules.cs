using System.Text.RegularExpressions;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;

namespace Evidgate.Modules.Orchestrator.Application.Attempts;

/// <summary>
/// The pure rules of the case-attempt writer (05.3.3-T2.1): the per-attempt validation table, the raw-status
/// normalisation and the payload equality a duplicate is decided on. No I/O and no tenant scope, so the integration
/// tests can exercise it directly through <c>InternalsVisibleTo</c>.
/// </summary>
internal static partial class CaseAttemptRules
{
    /// <summary>The reason token of a malformed execution key (05.3.3-T2.1 step 2).</summary>
    internal const string ExecutionKeyInvalid = "execution_key_invalid";

    /// <summary>The reason token of an attempt number below 1 (05.3.3-T2.1 step 2).</summary>
    internal const string AttemptNoInvalid = "attempt_no_invalid";

    /// <summary>The reason token of an unstorable raw status (05.3.3-T2.1 step 2).</summary>
    internal const string RawStatusInvalid = "raw_status_invalid";

    /// <summary>The reason token of a negative duration (05.3.3-T2.1 step 2).</summary>
    internal const string DurationInvalid = "duration_invalid";

    /// <summary>The reason token of a finish before the start (05.3.3-T2.1 step 2).</summary>
    internal const string TimestampsInvalid = "timestamps_invalid";

    /// <summary>The largest <c>raw_status</c> the column accepts, in code points (migration 0032's <c>char_length</c> CHECK).</summary>
    internal const int MaxRawStatusCodePoints = 64;

    /// <summary>
    /// The 00 §7 <c>execution_key</c> shape: 64 lowercase hex characters. Anchored with <c>\A..\z</c> because .NET's
    /// <c>$</c> also matches before a trailing newline while the 0032 CHECK (<c>~ '^[0-9a-f]{64}$'</c>) does not.
    /// </summary>
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionKeyShape();

    /// <summary>True when <paramref name="key"/> is exactly 64 lowercase hex characters (the 0032 CHECK shape).</summary>
    /// <param name="key">The execution key, or null.</param>
    internal static bool IsValidExecutionKey(string? key) => key is not null && ExecutionKeyShape().IsMatch(key);

    /// <summary>
    /// The reason <paramref name="attempt"/> cannot be written, or null when it is well formed. The checks run in the
    /// documented token order, never mutate the attempt and never throw; the raw status is never trimmed or altered.
    /// </summary>
    /// <param name="attempt">The attempt to check.</param>
    internal static string? Validate(CaseAttemptInput attempt)
    {
        if (!IsValidExecutionKey(attempt.ExecutionKey))
        {
            return ExecutionKeyInvalid;
        }

        if (attempt.AttemptNo < 1)
        {
            return AttemptNoInvalid;
        }

        if (!IsValidRawStatus(attempt.RawStatus))
        {
            return RawStatusInvalid;
        }

        if (attempt.DurationMs is < 0)
        {
            return DurationInvalid;
        }

        if (attempt.StartedAt is { } started && attempt.FinishedAt is { } finished && Truncate(started) > Truncate(finished))
        {
            return TimestampsInvalid;
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="raw"/> is a storable raw status: at most 64 code points, no U+0000 and no lone
    /// surrogate. The count is of code points (runes), not UTF-16 units, matching PostgreSQL's <c>char_length</c>, so
    /// an astral character counts once and a surrogate pair is one code point.
    /// </summary>
    /// <param name="raw">The raw status exactly as the producer sent it, or null.</param>
    internal static bool IsValidRawStatus(string? raw)
    {
        if (raw is null)
        {
            return false;
        }

        var codePoints = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= raw.Length || !char.IsLowSurrogate(raw[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }

            if (++codePoints > MaxRawStatusCodePoints)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The payload a duplicate is decided on (05.3.3-T2.1 step 3): the raw status, the normalised status the server
    /// computes (the <c>xfail</c> fold included), the two timestamps truncated to the microseconds PostgreSQL stores,
    /// the duration and the job id. Two attempts of one slot are the same row when their payloads are equal.
    /// </summary>
    /// <param name="attempt">The attempt.</param>
    internal static CaseAttemptPayload PayloadOf(CaseAttemptInput attempt) => new(
        attempt.RawStatus,
        CaseAttemptStatus.Normalize(attempt.RawStatus, attempt.Xfail),
        Truncate(attempt.StartedAt),
        Truncate(attempt.FinishedAt),
        attempt.DurationMs,
        attempt.JobId);

    /// <summary>Truncates an instant to whole microseconds, flooring, so the value sent is the value PostgreSQL stores.</summary>
    /// <param name="value">The instant.</param>
    internal static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        var remainder = ticks % TimeSpan.TicksPerMicrosecond;
        if (remainder != 0)
        {
            ticks -= remainder < 0 ? remainder + TimeSpan.TicksPerMicrosecond : remainder;
        }

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>Truncates an optional instant to whole microseconds, preserving null.</summary>
    /// <param name="value">The instant, or null.</param>
    internal static DateTimeOffset? Truncate(DateTimeOffset? value) => value is { } instant ? Truncate(instant) : null;
}

/// <summary>
/// The stored payload of one case attempt, compared for the duplicate decision (05.3.3-T2.1 step 3/5). The timestamps
/// are already truncated to microseconds, so two payloads compare as equal exactly when the rows they describe are
/// the same. Records give ordinal string equality and instant equality for the two <see cref="DateTimeOffset"/> values.
/// </summary>
/// <param name="RawStatus">The raw status as the producer sent it.</param>
/// <param name="NormalizedStatus">The server-normalised status.</param>
/// <param name="StartedAt">The start, truncated to microseconds, or null.</param>
/// <param name="FinishedAt">The finish, truncated to microseconds, or null.</param>
/// <param name="DurationMs">The duration in milliseconds, or null.</param>
/// <param name="JobId">The runner job in agent mode, or null.</param>
internal readonly record struct CaseAttemptPayload(
    string RawStatus,
    string NormalizedStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    Guid? JobId);

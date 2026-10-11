using System.Text.RegularExpressions;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The shape checks the artifact commands share (06.1.1 §5, 06.1.2 §5): the execution key, the checksum and the
/// media type, each mirroring a 0028 CHECK constraint, so a value the database would refuse never reaches a
/// statement (00 §5.5 C6). One definition, used by <see cref="CreateArtifactRecordValidator"/>,
/// <see cref="CreateArtifactRecordHandler"/> and <see cref="RequestArtifactUploadUrlHandler"/>, so the two
/// commands can never drift.
/// </summary>
internal static partial class ArtifactShape
{
    /// <summary>The 00 §7 execution-key shape: sha256 hex, lowercase (the 0028 <c>artifact_execution_key_check</c>).</summary>
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionKeyShape();

    /// <summary>The 00 §7 checksum shape: <c>sha256:</c> and 64 lowercase hex characters, anchored with <c>\z</c>.</summary>
    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumShape();

    /// <summary>True when <paramref name="value"/> is 64 lowercase hex characters (00 §7).</summary>
    public static bool IsExecutionKey(string? value) => value is not null && ExecutionKeyShape().IsMatch(value);

    /// <summary>True when <paramref name="value"/> is <c>sha256:&lt;64 lowercase hex&gt;</c> (00 §7).</summary>
    public static bool IsChecksum(string? value) => value is not null && ChecksumShape().IsMatch(value);

    /// <summary>
    /// True when <paramref name="value"/> is 1–255 code points: Postgres <c>char_length</c> counts code points,
    /// not UTF-16 units (the 0028 <c>artifact_media_type_check</c>).
    /// </summary>
    public static bool IsMediaType(string? value) =>
        value is not null && value.EnumerateRunes().Count() is >= 1 and <= 255;
}

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The parse runner's surface (06.3.6 T1.3, AC2/AC8), bound from <c>EVIDGATE__PARSE__*</c> (00 §9.2). Every value
/// here is a ceiling on the child, so an unusable value never widens a limit: a non-positive CPU seconds, wall
/// clock or memory limit is read as the documented default instead, and the two output caps fall back to a
/// derivation from the run's own input cap.
/// <para>
/// <b>Out of process.</b> <c>OUTOFPROCESS</c> gates registration of <see cref="IParseRunner"/>: with the flag off
/// nothing is registered and the in-process <c>IParsing</c> path is untouched, which is the rollback 06.3.6 §11
/// names. The default is off — no call site resolves the runner before 06.3.6-T1.4, and a host that has not
/// opted in must not start a process it never uses.
/// </para>
/// <para>
/// <b>Output caps.</b> The child's amplification is not bounded by its input: 06.3.6-T1.1+T1.2 measured up to
/// 18.6x input bytes, about 10M records from a 16 MiB input and non-ASCII escapes at about 3x (plan Notes,
/// S117). The parent therefore caps the child's stdout in bytes <em>and</em> in records, and a breach is a
/// failure, never a truncated success. Both caps default to a derivation from the run's <c>--max-bytes</c>:
/// <list type="bullet">
/// <item>bytes: <c>max(8 MiB, 24 x max-bytes)</c>, never above 256 MiB;</item>
/// <item>records: <c>max(4 096, max-bytes / 8)</c>, never above 1 000 000;</item>
/// <item>one record line: <c>min(byte cap, max(1 MiB, 4 x max-bytes))</c>, because a single text record may
/// legitimately carry a whole attribute value escaped to about 3x its bytes.</item>
/// </list>
/// The 24x factor clears the measured legit ceiling (18.6x) and the hard ceilings bound the parent's own heap:
/// the buffered stream costs one copy of the child's stdout, so 256 MiB of stdout is the most one run can hold.
/// An artifact whose legit stream exceeds the derived cap ends as a breach; raising the two options is the tuning
/// knob, and two processes parsing 100 MiB artifacts at once are a capacity decision, not a config one.
/// </para>
/// </summary>
internal sealed class ParseRunnerOptions
{
    /// <summary>The configuration section the binder reads; yields <c>EVIDGATE__PARSE__*</c> keys.</summary>
    public const string SectionName = "EVIDGATE:PARSE";

    /// <summary>Where the child assembly sits, relative to the host's base directory, in the worker image.</summary>
    public const string DefaultChildPath = "parser/Evidgate.Parser.dll";

    /// <summary>Default <c>--cpu-seconds</c>: RLIMIT_CPU soft and hard, in seconds.</summary>
    public const int DefaultCpuSeconds = 20;

    /// <summary>Default wall clock: the same 30 s the in-process profile allows (06.3.1 AC2, 06.3.6 AC2).</summary>
    public const int DefaultWallClockSeconds = 30;

    /// <summary>Default <c>DOTNET_GCHeapHardLimit</c>: sized from the measured RSS amplification (plan Notes, S117).</summary>
    public const long DefaultMemoryLimitBytes = 512L * 1024 * 1024;

    /// <summary>The smallest derived stdout byte cap: a normal report is kilobytes, so this is still generous.</summary>
    internal const long OutputBytesFloor = 8L * 1024 * 1024;

    /// <summary>The largest stdout the parent buffers for one run, whatever the input cap.</summary>
    internal const long OutputBytesCeiling = 256L * 1024 * 1024;

    /// <summary>The measured-amplification factor: above the 18.6x the child reached on hostile input.</summary>
    internal const long OutputAmplificationFactor = 24;

    /// <summary>The smallest derived record cap.</summary>
    internal const int RecordCountFloor = 4_096;

    /// <summary>
    /// The largest record count the parent buffers for one run. Set above the 16 MiB input that measured about 10M
    /// records (plan Notes :146), so a record-dense artifact that the child streamed legitimately is never called a
    /// breach; the byte cap stays the first wall for anything that streams real payload.
    /// </summary>
    internal const int RecordCountCeiling = 12_000_000;

    /// <summary>
    /// Records a run may emit per thousand input bytes: three quarters of one, from the ~10M records a 16 MiB
    /// token-dense input produced (plan Notes :146) — 0.6 per byte measured, rounded up for headroom.
    /// </summary>
    internal const int RecordsPerThousandInputBytes = 750;

    /// <summary>The smallest per-record line cap.</summary>
    internal const long RecordLineFloor = 1L * 1024 * 1024;

    /// <summary>How much one record may grow over its input contribution (a whole text node, escaped).</summary>
    internal const long RecordLineFactor = 4;

    /// <summary>Whether the out-of-process runner is registered; off means the in-process path is the only one.</summary>
    public bool OutOfProcess { get; set; }

    /// <summary>
    /// The child assembly: a relative path resolves against <see cref="AppContext.BaseDirectory"/> (the worker image
    /// publishes it to <c>/app/parser/</c>), an absolute path is used as it stands.
    /// </summary>
    public string ChildPath { get; set; } = DefaultChildPath;

    /// <summary>RLIMIT_CPU seconds the child applies to itself; a non-positive value means the default.</summary>
    public int CpuSeconds { get; set; } = DefaultCpuSeconds;

    /// <summary>
    /// The wall clock the parent enforces by tree-killing the child; a non-positive value means the default. This
    /// is the only bound on the emit pass, because the profile's own 30 s cap is cooperative (plan Notes, S117).
    /// </summary>
    public int WallClockSeconds { get; set; } = DefaultWallClockSeconds;

    /// <summary>The child's <c>DOTNET_GCHeapHardLimit</c> in bytes; a non-positive value means the default.</summary>
    public long MemoryLimitBytes { get; set; } = DefaultMemoryLimitBytes;

    /// <summary>
    /// The most stdout bytes one run may write; <c>0</c> derives the cap from the run's input cap (see the type
    /// remarks). A positive value is used as it stands.
    /// </summary>
    public long MaxOutputBytes { get; set; }

    /// <summary>
    /// The most record lines one run may write; <c>0</c> derives the cap from the run's input cap (see the type
    /// remarks). A positive value is used as it stands.
    /// </summary>
    public int MaxOutputRecords { get; set; }

    /// <summary>
    /// The directory per-run temp directories are created under; empty means the OS temp directory
    /// (<c>TMPDIR</c> when the deployment sets one, which the chiseled image does not need).
    /// </summary>
    public string TempRoot { get; set; } = string.Empty;

    /// <summary>The effective RLIMIT_CPU seconds, defaulted when the configured value is not positive.</summary>
    internal int EffectiveCpuSeconds => CpuSeconds > 0 ? CpuSeconds : DefaultCpuSeconds;

    /// <summary>The effective wall clock, defaulted when the configured value is not positive.</summary>
    internal TimeSpan EffectiveWallClock =>
        TimeSpan.FromSeconds(WallClockSeconds > 0 ? WallClockSeconds : DefaultWallClockSeconds);

    /// <summary>The effective heap hard limit, defaulted when the configured value is not positive.</summary>
    internal long EffectiveMemoryLimitBytes =>
        MemoryLimitBytes > 0 ? MemoryLimitBytes : DefaultMemoryLimitBytes;

    /// <summary>The stdout byte cap for a run whose input cap is <paramref name="maxBytes"/>.</summary>
    /// <param name="maxBytes">The run's <c>--max-bytes</c> (positive).</param>
    /// <returns>The configured cap, or the derived one clamped to the floor and the ceiling.</returns>
    internal long EffectiveMaxOutputBytes(long maxBytes)
    {
        if (MaxOutputBytes > 0)
        {
            return MaxOutputBytes;
        }

        // Written as a comparison rather than a multiplication so an enormous input cap cannot overflow into a
        // small derived cap.
        var scaled = maxBytes >= OutputBytesCeiling / OutputAmplificationFactor
            ? OutputBytesCeiling
            : OutputAmplificationFactor * maxBytes;

        return Math.Clamp(scaled, OutputBytesFloor, OutputBytesCeiling);
    }

    /// <summary>The record cap for a run whose input cap is <paramref name="maxBytes"/>.</summary>
    /// <param name="maxBytes">The run's <c>--max-bytes</c> (positive).</param>
    /// <returns>The configured cap, or the derived one clamped to the floor and the ceiling.</returns>
    internal int EffectiveMaxOutputRecords(long maxBytes)
    {
        if (MaxOutputRecords > 0)
        {
            return MaxOutputRecords;
        }

        // A comparison before the multiplication, so an enormous input cap cannot overflow into a small derived cap.
        var scaled = maxBytes >= (long)RecordCountCeiling * 1000 / RecordsPerThousandInputBytes
            ? RecordCountCeiling
            : maxBytes * RecordsPerThousandInputBytes / 1000;

        return (int)Math.Clamp(scaled, RecordCountFloor, RecordCountCeiling);
    }

    /// <summary>The longest single record line the runner accepts for a run whose input cap is <paramref name="maxBytes"/>.</summary>
    /// <param name="maxBytes">The run's <c>--max-bytes</c> (positive).</param>
    /// <returns>
    /// The per-line cap: four times the input cap while that stays under the line floor, the floor above it, and
    /// never more than the run's byte cap.
    /// </returns>
    internal int EffectiveMaxRecordBytes(long maxBytes)
    {
        var line = maxBytes >= RecordLineFloor / RecordLineFactor
            ? RecordLineFloor
            : RecordLineFactor * maxBytes;

        return (int)Math.Min(EffectiveMaxOutputBytes(maxBytes), line);
    }
}

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
/// <item>records: <c>max(4 096, max-bytes / 8)</c>, never above 2 000 000;</item>
/// <item>one record line: <c>min(byte cap, max(1 MiB, 6 x max-bytes))</c>, because one JSON record carries the
/// whole document, escaped at about 3x for non-ASCII values, so 6x still admits a document that fills the
/// input cap.</item>
/// </list>
/// The 24x factor is headroom in the derivation, not a promise about streams: the derived cap stops growing at
/// <c>256 MiB / 24</c> (~10.7 MiB of input), and at the measured 18.6x amplification a stream only passes the
/// 256 MiB ceiling from <c>256 MiB / 18.6</c> (~13.8 MiB of input) upward — an input between the two knees still
/// succeeds. Above that cutoff the run ends as an output-bytes breach by design: 256 MiB is what bounds the
/// parent's own heap, since the buffered stream costs one copy of the child's stdout, and the accepted records cost
/// their payload plus about 56 bytes of array overhead each — at the 2 000 000 record ceiling that overhead alone is
/// about 112 MiB. The record ceiling is set below the ~10M records the measured hostile 16 MiB input produced, so
/// that shape is refused rather than buffered. An artifact whose legit stream exceeds either derived cap ends as a
/// breach; raising the two options is the tuning knob, and two processes parsing 100 MiB artifacts at once are a
/// capacity decision, not a config one.
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
    /// The largest record count the parent buffers for one run. Deliberately below the ~10M records a 16 MiB
    /// token-dense input measured (plan Notes :146): that shape is refused as a breach instead of buffered, because
    /// the accepted records are the parent's memory — each one costs its payload plus about 56 bytes of array
    /// overhead, so 2 000 000 records are about 112 MiB of overhead before any payload.
    /// </summary>
    internal const int RecordCountCeiling = 2_000_000;

    /// <summary>
    /// Input bytes per allowed record: one record per eight bytes of the run's cap. The derivation reads the cap, not
    /// a measurement, so a record-dense stream is bounded by the input it came from rather than by a density the
    /// child controls.
    /// </summary>
    internal const int InputBytesPerRecord = 8;

    /// <summary>The smallest per-record line cap.</summary>
    internal const long RecordLineFloor = 1L * 1024 * 1024;

    /// <summary>
    /// How much one record may grow over the input cap: a JSON record carries the whole document, and a non-ASCII
    /// value escapes to about 3x, so 6x admits a document that fills the cap with the wrapper's own bytes on top.
    /// </summary>
    internal const long RecordLineFactor = 6;

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
    /// <returns>
    /// The configured cap, or the derived one clamped to the floor and the ceiling. The derivation reaches the
    /// ceiling at <c>256 MiB / 24</c> (~10.7 MiB of input) and the cap then stops growing; at the measured 18.6x
    /// amplification a stream passes 256 MiB only from <c>256 MiB / 18.6</c> (~13.8 MiB of input), so an input above
    /// that cutoff ends as a breach by design rather than widening the parent's buffer.
    /// </returns>
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
    /// <returns>
    /// The configured cap, or <c>min(2 000 000, max(4 096, max-bytes / 8))</c>, clamped to the floor and the ceiling.
    /// </returns>
    internal int EffectiveMaxOutputRecords(long maxBytes)
    {
        if (MaxOutputRecords > 0)
        {
            return MaxOutputRecords;
        }

        return (int)Math.Clamp(maxBytes / InputBytesPerRecord, RecordCountFloor, RecordCountCeiling);
    }

    /// <summary>The longest single record line the runner accepts for a run whose input cap is <paramref name="maxBytes"/>.</summary>
    /// <param name="maxBytes">The run's <c>--max-bytes</c> (positive).</param>
    /// <returns>
    /// The per-line cap: <c>min(byte cap, max(1 MiB, 6 x max-bytes))</c>. The floor keeps a small input from
    /// refusing a record that a large document legitimately produces, the factor covers the escaping of a whole
    /// document carried in one record, and the run's byte cap still bounds the line.
    /// </returns>
    internal int EffectiveMaxRecordBytes(long maxBytes)
    {
        // A comparison before the multiplication, so an enormous input cap cannot overflow into a small line cap.
        var grown = maxBytes >= int.MaxValue / RecordLineFactor ? int.MaxValue : RecordLineFactor * maxBytes;
        var line = Math.Max(RecordLineFloor, grown);

        // The result is bounded by the line, which is at most int.MaxValue, so the cast cannot truncate.
        return (int)Math.Min(EffectiveMaxOutputBytes(maxBytes), line);
    }
}

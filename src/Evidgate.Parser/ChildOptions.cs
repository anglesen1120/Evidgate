using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;

namespace Evidgate.Parser;

/// <summary>The profile the child runs (06.3.6 §5: <c>evidgate-parser --profile …</c>).</summary>
internal enum ParseProfile
{
    /// <summary>One XML report, validated by the 06.3.1 profile, streamed as start/text/end records.</summary>
    Xml,

    /// <summary>One JSON payload, parsed by the 06.3.1 profile, emitted as one document record.</summary>
    Json,

    /// <summary>One zip archive, walked by the 06.3.1 profile, emitted as one record per entry.</summary>
    Archive,
}

/// <summary>
/// The child's command line (06.3.6 §5, §7):
/// <c>Evidgate.Parser --profile xml|json|archive --max-bytes &lt;bytes&gt; [--media-type &lt;declared type&gt;]
/// [--cpu-seconds &lt;seconds&gt;] [--max-file-bytes &lt;bytes&gt;]</c>.
/// <para>
/// Parsing is strict and total: the profile, the byte cap and the three optional values are the whole contract, so
/// an unknown switch, a repeated switch, a missing value (including a switch where a value belongs) or a value
/// outside its range is a usage error and never a silently different run. The launcher hands the caps in as
/// arguments — the child reads no environment variable and no file (06.3.6 AC1); a cap above the profile's own
/// ceiling and up to <see cref="long.MaxValue"/> is clamped rather than refused, so an enormous value still
/// yields a bounded run, while a digit string beyond <see cref="long.MaxValue"/> is not a number the child can
/// carry and is refused as a usage error (exit 2) like any other value the contract cannot honour.
/// </para>
/// <para>
/// The two resource switches are optional and apply nothing when absent (a developer run and the protocol tests
/// want the host's own limits): <c>--cpu-seconds</c> is a positive CPU-second budget, <c>--max-file-bytes</c> is
/// the largest file the child may create and accepts 0 — a run that may write no file at all, which is the
/// production shape. Either one present also sets <c>RLIMIT_CORE = 0</c>
/// (<see cref="ResourceLimits.TryApply"/>), so a limit run never dumps core. They are parsed here, not applied
/// here: applying them is the entry point's first act and a failure is exit 3, not a usage error.
/// </para>
/// </summary>
internal sealed class ChildOptions
{
    private const string ProfileOption = "--profile";
    private const string MaxBytesOption = "--max-bytes";
    private const string MediaTypeOption = "--media-type";
    private const string CpuSecondsOption = "--cpu-seconds";
    private const string MaxFileBytesOption = "--max-file-bytes";
    private const string MaxCasesOption = "--max-cases";

    private ChildOptions(
        ParseProfile profile,
        long maxBytes,
        string? mediaType,
        long? cpuSeconds,
        long? maxFileBytes,
        long maxCases)
    {
        Profile = profile;
        MaxBytes = maxBytes;
        MaxCases = maxCases;
        MediaType = mediaType;
        CpuSeconds = cpuSeconds;
        MaxFileBytes = maxFileBytes;
    }

    /// <summary>Which arm of the profile this run applies.</summary>
    public ParseProfile Profile { get; }

    /// <summary>The report entry cap (<c>--max-cases</c>), defaulting to the profile's published value.</summary>
    public long MaxCases { get; }

    /// <summary>
    /// The per-kind byte cap from <c>artifact_kind_policy.max_bytes</c> (always positive), clamped to the largest
    /// cap the profile can honour (<see cref="BoundedContent.EffectiveCap"/>).
    /// </summary>
    public long MaxBytes { get; }

    /// <summary>
    /// The artifact's declared media type, or <see langword="null"/> when the caller already sniffed it. When it
    /// is given the profile's sniff runs before the parse and a mismatch is a rejection (06.3.1 AC5).
    /// </summary>
    public string? MediaType { get; }

    /// <summary>
    /// The CPU-second budget for <c>RLIMIT_CPU</c> (<c>--cpu-seconds</c>), or <see langword="null"/> when the
    /// launcher did not ask for one: the child then inherits the launcher's limit.
    /// </summary>
    public long? CpuSeconds { get; }

    /// <summary>
    /// The largest file the child may create for <c>RLIMIT_FSIZE</c> (<c>--max-file-bytes</c>), or
    /// <see langword="null"/> when the launcher did not ask for one. 0 is a legal request — the production shape,
    /// in which the child may write no file at all.
    /// </summary>
    public long? MaxFileBytes { get; }

    /// <summary>
    /// Reads the argument vector against the contract. Every failure is the same answer — <see langword="false"/>
    /// — because the caller writes one fixed usage line and exits 2: the child never says which value it
    /// refused, so a hostile argument cannot make the child echo anything (06.3.6 §7).
    /// </summary>
    /// <param name="args">The argument vector, without the program name.</param>
    /// <param name="options">The parsed options when this returns true.</param>
    /// <returns>True when the vector names one profile, one positive byte cap and any optional value, and nothing
    /// else.</returns>
    public static bool TryParse(string[] args, [NotNullWhen(true)] out ChildOptions? options)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        ParseProfile? profile = null;
        long? maxBytes = null;
        string? mediaType = null;
        var mediaTypeGiven = false;
        long? cpuSeconds = null;
        long? maxFileBytes = null;
        long? maxCases = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case ProfileOption:
                    if (profile is not null
                        || !TryTakeValue(args, ref index, out var profileValue)
                        || !TryParseProfile(profileValue, out var parsedProfile))
                    {
                        return false;
                    }

                    profile = parsedProfile;
                    break;

                case MaxBytesOption:
                    if (maxBytes is not null
                        || !TryTakeValue(args, ref index, out var maxBytesValue)
                        || !TryParseMaxBytes(maxBytesValue, out var parsedMaxBytes))
                    {
                        return false;
                    }

                    maxBytes = parsedMaxBytes;
                    break;

                case MediaTypeOption:
                    if (mediaTypeGiven
                        || !TryTakeValue(args, ref index, out var mediaTypeValue)
                        || mediaTypeValue.Trim().Length == 0)
                    {
                        return false;
                    }

                    mediaTypeGiven = true;
                    mediaType = mediaTypeValue;
                    break;

                case CpuSecondsOption:
                    if (cpuSeconds is not null
                        || !TryTakeValue(args, ref index, out var cpuValue)
                        || !TryParseCpuSeconds(cpuValue, out var parsedCpuSeconds))
                    {
                        return false;
                    }

                    cpuSeconds = parsedCpuSeconds;
                    break;

                case MaxFileBytesOption:
                    if (maxFileBytes is not null
                        || !TryTakeValue(args, ref index, out var fileBytesValue)
                        || !TryParseMaxFileBytes(fileBytesValue, out var parsedMaxFileBytes))
                    {
                        return false;
                    }

                    maxFileBytes = parsedMaxFileBytes;
                    break;

                case MaxCasesOption:
                    if (maxCases is not null
                        || !TryTakeValue(args, ref index, out var maxCasesValue)
                        || !long.TryParse(maxCasesValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMaxCases)
                        || parsedMaxCases <= 0)
                    {
                        return false;
                    }

                    maxCases = parsedMaxCases;
                    break;

                default:
                    // A positional argument or an unknown switch: the contract has exactly six options.
                    return false;
            }
        }

        if (profile is null || maxBytes is null)
        {
            return false;
        }

        options = new ChildOptions(
            profile.Value,
            maxBytes.Value,
            mediaType,
            cpuSeconds,
            maxFileBytes,
            maxCases ?? ParsingService.DefaultMaxReportCases);

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;

        if (index + 1 >= args.Length)
        {
            return false;
        }

        var candidate = args[index + 1];
        if (candidate.StartsWith("--", StringComparison.Ordinal))
        {
            // A switch where a value belongs is a missing value, not a value that happens to look like one:
            // `--media-type --media-type text/xml` must not consume the second switch as the declared type and
            // leave the real type as a positional argument (which would report the wrong reason).
            return false;
        }

        index++;
        value = candidate;

        return true;
    }

    private static bool TryParseProfile(string value, out ParseProfile profile)
    {
        switch (value)
        {
            case "xml":
                profile = ParseProfile.Xml;

                return true;

            case "json":
                profile = ParseProfile.Json;

                return true;

            case "archive":
                profile = ParseProfile.Archive;

                return true;

            default:
                profile = default;

                return false;
        }
    }

    private static bool TryParseMaxBytes(string value, out long maxBytes)
    {
        // Decimal digits only: an explicit sign, whitespace or a thousands separator is not the contract, and
        // the invariant parse reads the same digits as the same number on every host culture.
        maxBytes = 0;

        if (value.Length == 0
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        // Clamped, not refused: the profile cannot honour a cap above its own contiguous-read ceiling
        // (`BoundedContent.EffectiveCap` is Array.MaxLength - 1, one byte below the largest array so the extra
        // byte that proves an over-cap stream still fits), so a larger --max-bytes would make this child buffer
        // toward the heap limit before the profile ever applied its clamp. Clamping keeps the run bounded and
        // changes no verdict: an object above the clamp is refused as size_exceeded either way. The media sniff's
        // 4 KiB prefix is a separate array (ParsingLimits.SniffPrefixBytes), so it costs the cap nothing.
        maxBytes = BoundedContent.EffectiveCap(parsed);

        return true;
    }

    /// <summary>
    /// <c>--cpu-seconds</c>: a positive decimal count of CPU seconds. Zero, a sign, whitespace, a fraction or a
    /// digit string beyond <see cref="long.MaxValue"/> is a usage error — the kernel's <c>rlim_t</c> is 64-bit, so
    /// every value this parse accepts fits it exactly, and 0 would mean "no CPU at all", which is a refused run
    /// rather than a limit.
    /// </summary>
    /// <param name="value">The switch's value.</param>
    /// <param name="cpuSeconds">The accepted value when this returns true.</param>
    /// <returns>True when the value is a positive decimal integer.</returns>
    private static bool TryParseCpuSeconds(string value, out long cpuSeconds)
    {
        cpuSeconds = 0;

        if (value.Length == 0
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        cpuSeconds = parsed;

        return true;
    }

    /// <summary>
    /// <c>--max-file-bytes</c>: a non-negative decimal count of bytes. Unlike the profile's own cap, 0 is a legal
    /// value: the production run may create no file at all, and <c>RLIMIT_FSIZE = 0</c> is exactly how that is
    /// enforced. A sign, whitespace, a fraction or a digit string beyond <see cref="long.MaxValue"/> is a usage
    /// error, like every other value the contract cannot carry.
    /// </summary>
    /// <param name="value">The switch's value.</param>
    /// <param name="maxFileBytes">The accepted value when this returns true.</param>
    /// <returns>True when the value is a non-negative decimal integer.</returns>
    private static bool TryParseMaxFileBytes(string value, out long maxFileBytes)
    {
        maxFileBytes = 0;

        if (value.Length == 0
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            return false;
        }

        maxFileBytes = parsed;

        return true;
    }
}

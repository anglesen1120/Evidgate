using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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
/// <c>Evidgate.Parser --profile xml|json|archive --max-bytes &lt;bytes&gt; [--media-type &lt;declared type&gt;]</c>.
/// <para>
/// Parsing is strict and total: the profile, the byte cap and an optional declared media type are the whole
/// contract, so an unknown switch, a repeated switch, a missing value or a non-positive cap is a usage error
/// and never a silently different run. The launcher hands the caps in as arguments — the child reads no
/// environment variable and no file (06.3.6 AC1).
/// </para>
/// </summary>
internal sealed class ChildOptions
{
    private const string ProfileOption = "--profile";
    private const string MaxBytesOption = "--max-bytes";
    private const string MediaTypeOption = "--media-type";

    private ChildOptions(ParseProfile profile, long maxBytes, string? mediaType)
    {
        Profile = profile;
        MaxBytes = maxBytes;
        MediaType = mediaType;
    }

    /// <summary>Which arm of the profile this run applies.</summary>
    public ParseProfile Profile { get; }

    /// <summary>The per-kind byte cap from <c>artifact_kind_policy.max_bytes</c> (always positive).</summary>
    public long MaxBytes { get; }

    /// <summary>
    /// The artifact's declared media type, or <see langword="null"/> when the caller already sniffed it. When it
    /// is given the profile's sniff runs before the parse and a mismatch is a rejection (06.3.1 AC5).
    /// </summary>
    public string? MediaType { get; }

    /// <summary>
    /// Reads the argument vector against the contract. Every failure is the same answer — <see langword="false"/>
    /// — because the caller writes one fixed usage line and exits 2: the child never says which value it
    /// refused, so a hostile argument cannot make the child echo anything (06.3.6 §7).
    /// </summary>
    /// <param name="args">The argument vector, without the program name.</param>
    /// <param name="options">The parsed options when this returns true.</param>
    /// <returns>True when the vector names one profile and one positive byte cap, and nothing else.</returns>
    public static bool TryParse(string[] args, [NotNullWhen(true)] out ChildOptions? options)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        ParseProfile? profile = null;
        long? maxBytes = null;
        string? mediaType = null;
        var mediaTypeGiven = false;

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

                default:
                    // A positional argument or an unknown switch: the contract has exactly three options.
                    return false;
            }
        }

        if (profile is null || maxBytes is null)
        {
            return false;
        }

        options = new ChildOptions(profile.Value, maxBytes.Value, mediaType);

        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;

            return false;
        }

        index++;
        value = args[index];

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

        return value.Length > 0
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maxBytes)
            && maxBytes > 0;
    }
}

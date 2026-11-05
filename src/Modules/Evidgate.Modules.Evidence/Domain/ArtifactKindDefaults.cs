using System.Diagnostics.CodeAnalysis;

namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The transport limit of <c>POST /v1/artifacts/upload-url</c> (06.1.2 §5, 06.1.2-T3.1): the endpoint refuses a
/// larger body with the 00 §9.2 413 <c>payload_too_large</c> envelope before it deserialises anything, so an
/// oversized body costs no parse, no statement and no row. The legal body — one artifact's coordinates, a media
/// type and a <c>sha256</c> digest — stays under 2.5 KiB even when every string is JSON-escaped, so 8 KiB is
/// generous headroom that still bounds a hostile caller.
/// <c>contracts/evidence/artifact-kind-defaults.v1.json</c> publishes the same number as
/// <c>request_body_max_bytes</c> (06.1.2-T3.2).
/// </summary>
internal static class UploadUrlLimits
{
    /// <summary>The largest request body the upload-url route accepts, in bytes.</summary>
    public const int MaxBodyBytes = 8 * 1024;
}

/// <summary>
/// The two bounds of <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c>. They belong together because the body
/// cap has to fit the rule count: 64 rules of a 128-character name cost 11,073 bytes of JSON, the 64 media types the
/// same body may carry cost at most 16,448, so 32 KiB holds the largest documented body and still bounds a caller.
/// </summary>
internal static class CapturePolicyLimits
{
    /// <summary>The most rules one kind may declare, so one write stays one bounded statement.</summary>
    public const int MaxRedactionRules = 64;

    /// <summary>The largest request body the kind-policy route accepts, in bytes; over it the answer is 413.</summary>
    public const int MaxBodyBytes = 32 * 1024;
}

/// <summary>
/// The static default of one artifact kind (06.1.2-T3.1, DECISIONS §8.1 D-A9): the size cap and the accepted media
/// types a project without an <c>artifact_kind_policy</c> row for the kind gets. The media types are exactly the
/// ones <c>MediaSniffer.HasSignature</c> can verify from the object's leading bytes (06.3.1-T2.2), so a default
/// never admits a type the completion must fail closed on.
/// </summary>
/// <param name="MaxBytes">The kind's default size cap, in bytes.</param>
/// <param name="MediaTypes">The kind's default accepted media types.</param>
internal sealed record ArtifactKindDefault(long MaxBytes, string[] MediaTypes);

/// <summary>
/// The facts one <c>artifact_kind_policy</c> row supplies to <see cref="ArtifactKindDefaults.Resolve"/>
/// (06.1.2-T3.1), as each of the two consumers reads them: the mint's EF row and the completion's left-joined
/// columns. A null <see cref="KindPolicyRow"/> — never a member being null — means the project has no row for the
/// kind, which is also how both consumers detect it (<c>artifact.max_bytes</c>/<c>media_types</c>/
/// <c>capture_enabled</c>/<c>default_sensitivity</c> are NOT NULL together, migration 0028).
/// </summary>
/// <param name="MaxBytes">The row's own size cap; a defaulted kind's effective cap is the lower of the two.</param>
/// <param name="MediaTypes">The row's accepted media types; they replace the default list entirely.</param>
/// <param name="CaptureEnabled">The row's capture switch; false refuses the kind however the defaults read.</param>
/// <param name="DefaultSensitivity">The row's sensitivity, which a minted artifact inherits.</param>
internal readonly record struct KindPolicyRow(
    long MaxBytes,
    string[] MediaTypes,
    bool CaptureEnabled,
    string DefaultSensitivity);

/// <summary>
/// The policy a project effectively has for one kind (06.1.2-T3.1), after
/// <see cref="ArtifactKindDefaults.Resolve"/> has combined the project's optional row with the static defaults.
/// Both consumers use this and nothing else: the mint's cap/media-type refusals and the completion's re-check
/// (the <c>too_large</c>/<c>media_type_mismatch</c> verdicts and the resolved cap they compare against) can never
/// disagree, because there is one resolution rule.
/// </summary>
/// <param name="MaxBytes">The effective size cap, in bytes; a declared/stored length above it is refused.</param>
/// <param name="MediaTypes">The effective accepted media types (bare, compared case-insensitively).</param>
/// <param name="CaptureEnabled">The effective capture switch; false is the existing 422 <c>kind_disabled</c>.</param>
/// <param name="DefaultSensitivity">The effective sensitivity a minted artifact inherits.</param>
internal sealed record EffectiveKindPolicy(
    long MaxBytes,
    string[] MediaTypes,
    bool CaptureEnabled,
    string DefaultSensitivity);

/// <summary>
/// The static per-kind defaults of DECISIONS §8.1 D-A9 and their resolution against a project's
/// <c>artifact_kind_policy</c> row (06.1.2-T3.1, 06.1.2 §10 T3.1 "seeded as <c>artifact_kind_policy</c> fallback
/// when no project row exists"). Four of the eight 00 §7 kinds get a default:
/// <list type="table">
/// <item><term><c>trace</c></term><description>100 MiB, <c>application/zip</c></description></item>
/// <item><term><c>video</c></term><description>200 MiB, <c>video/mp4</c></description></item>
/// <item><term><c>screenshot</c></term><description>10 MiB, <c>image/png</c>, <c>image/jpeg</c></description></item>
/// <item><term><c>raw_report</c></term><description>256 MiB, the XML family, <c>application/json</c>, <c>text/html</c>, <c>application/zip</c></description></item>
/// </list>
/// D-A9's <c>html_report</c> (100 MiB) folds into <c>raw_report</c>, whose 256 MiB cap is the higher of the two;
/// D-A9's <c>log</c> (20 MiB) has no kind in the 00 §7 closed list and is therefore unmapped, not implemented.
/// <para>
/// <c>dom_snapshot</c>, <c>a11y_snapshot</c>, <c>patch</c> and <c>ui_hierarchy</c> deliberately have NO default:
/// with no project row they keep the fail-closed 422 <c>kind_disabled</c>, because D-A9 fixes no cap for them
/// (the owner decision for <c>ui_hierarchy</c> arrives with 06.3.5-T1.1 and the per-kind policy admin with
/// 06.2.1). Nobody guesses a cap for them; the resolution returns null and the caller's existing refusal stands.
/// </para>
/// <para>
/// The resolution rules are: no row and no default is null; no row and a default is the default with capture on
/// and <see cref="ArtifactSensitivity.ForKind"/>(kind); a row always supplies the media types, the capture switch
/// and the sensitivity, and for a kind that also has a default the effective cap is the LOWER of the row's and
/// the default's (D-A9: "configurable down, never up without a decision"), while a row for a kind without a
/// default is used exactly as it is.
/// </para>
/// </summary>
internal static class ArtifactKindDefaults
{
    /// <summary>The XML family's canonical type; the sniff also accepts <c>*/*+xml</c> (06.3.1-T2.2).</summary>
    private const string XmlType = "application/xml";

    /// <summary>The XML family's text spelling (a JUnit report is often served as <c>text/xml</c>).</summary>
    private const string TextXmlType = "text/xml";

    /// <summary>The JSON family's canonical type; the sniff also accepts <c>application/*+json</c>.</summary>
    private const string JsonType = "application/json";

    /// <summary>The HTML report type (00 §7's <c>html_report</c> lands here, see the class remarks).</summary>
    private const string HtmlType = "text/html";

    /// <summary>The Playwright trace archive.</summary>
    private const string ZipType = "application/zip";

    /// <summary>A screenshot's PNG.</summary>
    private const string PngType = "image/png";

    /// <summary>A screenshot's JPEG.</summary>
    private const string JpegType = "image/jpeg";

    /// <summary>The recorded video type (the only video container the sniffer recognises).</summary>
    private const string Mp4Type = "video/mp4";

    /// <summary>A trace archive is one zip.</summary>
    private static readonly string[] TraceTypes = [ZipType];

    /// <summary>The one video container the sniffer recognises.</summary>
    private static readonly string[] VideoTypes = [Mp4Type];

    /// <summary>The two image types a screenshot may be.</summary>
    private static readonly string[] ScreenshotTypes = [PngType, JpegType];

    /// <summary>The XML family, JSON, HTML and the plain zip a runner report may be.</summary>
    private static readonly string[] RawReportTypes = [XmlType, TextXmlType, JsonType, HtmlType, ZipType];

    /// <summary>
    /// The table, in the 00 §7 contract order of the kinds that have a default: the single source both the lookup
    /// and <see cref="Kinds"/> are built from, so the published contract file (06.1.2-T3.2) and the code cannot
    /// drift. It is declared after the media-type lists because static initialisers run in textual order.
    /// </summary>
    private static readonly (string Kind, ArtifactKindDefault Default)[] Table =
    [
        (ArtifactKind.Trace, new ArtifactKindDefault(100L * 1024 * 1024, TraceTypes)),
        (ArtifactKind.Video, new ArtifactKindDefault(200L * 1024 * 1024, VideoTypes)),
        (ArtifactKind.Screenshot, new ArtifactKindDefault(10L * 1024 * 1024, ScreenshotTypes)),
        (ArtifactKind.RawReport, new ArtifactKindDefault(256L * 1024 * 1024, RawReportTypes)),
    ];

    private static readonly Dictionary<string, ArtifactKindDefault> ByKind =
        Table.ToDictionary(entry => entry.Kind, entry => entry.Default, StringComparer.Ordinal);

    /// <summary>The kinds that have a static default, in the 00 §7 contract order (06.1.2-T3.2 publishes them).</summary>
    public static IReadOnlyList<string> Kinds { get; } = [.. Table.Select(entry => entry.Kind)];

    /// <summary>The kinds that have a static default, and their defaults.</summary>
    public static IReadOnlyDictionary<string, ArtifactKindDefault> All => ByKind;

    /// <summary>
    /// The static default of <paramref name="kind"/>, or false when the kind has none (the four fail-closed kinds,
    /// and any kind outside the 00 §7 closed list).
    /// </summary>
    /// <param name="kind">One of <see cref="ArtifactKind.All"/>.</param>
    /// <param name="default">The kind's default when it has one; null otherwise.</param>
    public static bool TryGet(string kind, [NotNullWhen(true)] out ArtifactKindDefault? @default)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (ByKind.TryGetValue(kind, out var found))
        {
            @default = found;
            return true;
        }

        @default = null;
        return false;
    }

    /// <summary>
    /// The effective policy of one kind: the project's row and the static defaults combined by the rules in this
    /// type's remarks. Null means the project has no row for the kind AND the kind has no default — the caller
    /// refuses with its existing 422 <c>kind_disabled</c>, the same answer a row with <c>capture_enabled</c> false
    /// gets.
    /// </summary>
    /// <param name="kind">One of <see cref="ArtifactKind.All"/>.</param>
    /// <param name="row">The project's row for the kind, or null when it has none.</param>
    /// <returns>The effective policy, or null when neither a row nor a default exists.</returns>
    public static EffectiveKindPolicy? Resolve(string kind, KindPolicyRow? row)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (TryGet(kind, out var @default))
        {
            // A row for a defaulted kind may only LOWER its cap; it still supplies the media types, the capture
            // switch and the sensitivity.
            return row is { } withDefault
                ? new EffectiveKindPolicy(
                    Math.Min(withDefault.MaxBytes, @default.MaxBytes),
                    withDefault.MediaTypes,
                    withDefault.CaptureEnabled,
                    withDefault.DefaultSensitivity)
                : new EffectiveKindPolicy(
                    @default.MaxBytes,
                    @default.MediaTypes,
                    CaptureEnabled: true,
                    ArtifactSensitivity.ForKind(kind));
        }

        // No default: only the project's own row can enable the kind, and it is used exactly as it is.
        return row is { } withoutDefault
            ? new EffectiveKindPolicy(
                withoutDefault.MaxBytes,
                withoutDefault.MediaTypes,
                withoutDefault.CaptureEnabled,
                withoutDefault.DefaultSensitivity)
            : null;
    }
}

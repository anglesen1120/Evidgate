using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The identity a run permanently records about what it was created for (05.2.2 §4, T1.3): the seven
/// <c>run</c> columns <c>ref_kind</c>, <c>ref_name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>base_sha</c>,
/// <c>trigger_event</c>, <c>is_fork</c> plus <see cref="UnverifiedRef"/>. Every value is written once in the create
/// transaction; the migration's trigger refuses any later change (AC5).
/// </summary>
/// <param name="RefKind">
/// <c>branch</c>, <c>tag</c> or <c>pull_request</c>, or null when nothing identified the ref. The read model and the
/// outbox member are <c>kind</c> (the wire drops the column prefix, 05.2.2 §5).
/// </param>
/// <param name="RefName">
/// The ref's own name: the branch of <c>refs/heads/&lt;n&gt;</c>, the tag of <c>refs/tags/&lt;n&gt;</c>, or GitHub's
/// <c>GITHUB_REF_NAME</c> form for a pull request (<c>&lt;N&gt;/merge</c>). Never more than 255 characters, so it
/// always fits <c>run.ref_name</c>.
/// </param>
/// <param name="PrNumber">The pull request number of a <c>refs/pull/&lt;N&gt;/merge</c> ref; null otherwise.</param>
/// <param name="HeadSha">
/// The commit the run was built from, as <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c>. It is the verified <c>sha</c> claim
/// only for a <c>branch</c>/<c>tag</c> ref: for a pull request GitHub reports the test-merge commit, which is not the
/// head, so the value comes from the body (or stays null).
/// </param>
/// <param name="BaseSha">The base commit; the body is the only source (no claim carries one).</param>
/// <param name="TriggerEvent">
/// <c>push</c>, <c>pull_request</c>, <c>schedule</c>, <c>manual</c> or <c>dispatch</c>; the body is the only source
/// (the exchange does not record <c>event_name</c>).
/// </param>
/// <param name="IsFork">
/// True only when the body says so: a fork PR exchange is refused 403 by 02.4.1, so a verified caller is never a fork
/// and the create path stores <c>false</c> for it, ignoring a conflicting body value.
/// </param>
/// <param name="UnverifiedRef">
/// True when at least one stored identity value came from the request body, i.e. the run carries an identity no
/// verified claim covers; such a run is advisory input, never a gating one (05.2.2 §8, 07). False when every value was
/// verified, and false when the request carried no identity at all (all fields null).
/// </param>
internal sealed record RunIdentity(
    string? RefKind,
    string? RefName,
    int? PrNumber,
    string? HeadSha,
    string? BaseSha,
    string? TriggerEvent,
    bool IsFork,
    bool UnverifiedRef);

/// <summary>
/// The optional <c>ref</c> object of the create body (05.2.2 §5), after its shape was validated (AC4): the seven wire
/// members <c>kind</c>, <c>name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>base_sha</c>, <c>trigger_event</c>,
/// <c>is_fork</c>. A member the body omits is null (except <see cref="IsFork"/>); an unknown member is a 422 before
/// this type exists.
/// </summary>
internal sealed record RunRefInput(
    string? Kind,
    string? Name,
    int? PrNumber,
    string? HeadSha,
    string? BaseSha,
    string? TriggerEvent,
    bool IsFork);

/// <summary>
/// The verified <c>ref</c>/<c>sha</c> pair of the caller's credential, read from the <c>ci_token_issue</c> row the
/// 02.4.1 GitHub OIDC exchange wrote from the token it had already verified (T1.3). It is the only source of a
/// <em>verified</em> identity: the body cannot reach the create path with one.
/// </summary>
/// <param name="Ref">The <c>ref</c> claim, e.g. <c>refs/heads/main</c> or <c>refs/pull/12/merge</c>.</param>
/// <param name="Sha">The <c>sha</c> claim: the commit the workflow ran from.</param>
internal sealed record CiRefClaim(string Ref, string Sha);

/// <summary>
/// The run-identity rules of 05.2.2-T1.3 as pure functions, so the trust policy is unit-testable without a database:
/// parse the body's <c>ref</c> block (shape only, AC4), map the verified <c>ref</c>/<c>sha</c> claim to the identity
/// members it covers, and merge the two under the claim-wins rule (AC1–AC3).
/// <para>
/// Trust policy (owner/coordinator decision D-S45-Q1, 05.2.2 §8):
/// (1) for the members a verified claim covers — <c>ref_kind</c>, <c>ref_name</c>, <c>pr_number</c>,
/// <c>is_fork</c> and (for a <c>branch</c>/<c>tag</c> ref) <c>head_sha</c> — the claim always wins and a conflicting
/// body value is ignored rather than refused, including when the claim's ref has a shape this version does not parse
/// (then those members stay null and the body is still ignored for them).
/// (2) <c>base_sha</c> and <c>trigger_event</c> have no verified source: they come from the body, or stay null.
/// (3) <c>unverified_ref</c> is true exactly when a stored member came from the body, so "verified claims only" is
/// false and "claims plus a body-only member" is true.
/// (4) A caller whose <c>ci_token_issue</c> row is missing is not verified at all: it is treated as a body-only
/// caller, never as a trusted one (fail toward advisory).
/// </para>
/// <para>
/// The claim's <c>ref</c>/<c>sha</c> are re-shaped here even though the exchange produced them from a verified token:
/// the exchange validates the ref against the binding's glob patterns (so a 512-character branch name is reachable)
/// but does not constrain the <c>sha</c> at all, and a value the column CHECKs refuse would surface as a 500. A value
/// this version cannot store is therefore dropped (null/absent) instead.
/// </para>
/// </summary>
internal static class RunIdentityRules
{
    /// <summary>The body member that carries the identity block (05.2.2 §5).</summary>
    internal const string RefMember = "ref";

    /// <summary>The longest <c>ref_name</c>, in code points — the <c>run.ref_name</c> CHECK (migration 0037).</summary>
    internal const int MaxRefNameLength = 255;

    /// <summary>The 00 §9.2 reason of a member whose value the run's shape rule (or the column CHECK) cannot hold.</summary>
    private const string InvalidValue = "invalid_value";

    /// <summary>The <c>ref_kind</c> values <c>run.ref_kind</c> admits (migration 0037).</summary>
    internal static readonly IReadOnlyList<string> Kinds = ["branch", "tag", "pull_request"];

    /// <summary>The <c>trigger_event</c> values <c>run.trigger_event</c> admits (migration 0037).</summary>
    internal static readonly IReadOnlyList<string> TriggerEvents = ["push", "pull_request", "schedule", "manual", "dispatch"];

    private const string BranchRefPrefix = "refs/heads/";
    private const string TagRefPrefix = "refs/tags/";
    private const string PullRefPrefix = "refs/pull/";
    private const string PullRefSuffix = "/merge";

    /// <summary>The <c>ref_kind</c> of a branch ref.</summary>
    internal const string BranchKind = "branch";

    /// <summary>The <c>ref_kind</c> of a tag ref.</summary>
    internal const string TagKind = "tag";

    /// <summary>The <c>ref_kind</c> of a pull-request ref.</summary>
    internal const string PullRequestKind = "pull_request";

    /// <summary>
    /// Parses the body's optional <c>ref</c> object, shape-checking every member (AC4). The body is validated whether
    /// or not a claim later overrides it, so a malformed block is always a 422 before any write.
    /// </summary>
    /// <param name="body">The request body root (already known to be a JSON object).</param>
    /// <returns>
    /// The parsed block (null when the body carries no <c>ref</c>) and the per-member failures; a non-empty list means
    /// the block is refused 422 <c>request_invalid</c> and the first return value is null.
    /// </returns>
    internal static (RunRefInput? Ref, List<ErrorDetail> Errors) ParseBodyRef(JsonElement body)
    {
        var errors = new List<ErrorDetail>();
        if (!body.TryGetProperty(RefMember, out var element))
        {
            return (null, errors);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ErrorDetail("/ref", InvalidValue));
            return (null, errors);
        }

        string? kind = null;
        string? name = null;
        int? prNumber = null;
        string? headSha = null;
        string? baseSha = null;
        string? triggerEvent = null;
        var isFork = false;

        foreach (var member in element.EnumerateObject())
        {
            switch (member.Name)
            {
                case "kind":
                    if (!TryString(member.Value, out kind) || !Kinds.Contains(kind, StringComparer.Ordinal))
                    {
                        errors.Add(new ErrorDetail("/ref/kind", InvalidValue));
                    }

                    break;

                case "name":
                    if (!TryString(member.Value, out name) || !IsStorableName(name))
                    {
                        errors.Add(new ErrorDetail("/ref/name", InvalidValue));
                    }

                    break;

                case "pr_number":
                    if (member.Value.ValueKind != JsonValueKind.Number
                        || !member.Value.TryGetInt32(out var number)
                        || number <= 0)
                    {
                        errors.Add(new ErrorDetail("/ref/pr_number", InvalidValue));
                    }
                    else
                    {
                        prNumber = number;
                    }

                    break;

                case "head_sha":
                    if (!TryString(member.Value, out headSha) || !IsSha(headSha))
                    {
                        errors.Add(new ErrorDetail("/ref/head_sha", InvalidValue));
                    }

                    break;

                case "base_sha":
                    if (!TryString(member.Value, out baseSha) || !IsSha(baseSha))
                    {
                        errors.Add(new ErrorDetail("/ref/base_sha", InvalidValue));
                    }

                    break;

                case "trigger_event":
                    if (!TryString(member.Value, out triggerEvent) || !TriggerEvents.Contains(triggerEvent, StringComparer.Ordinal))
                    {
                        errors.Add(new ErrorDetail("/ref/trigger_event", InvalidValue));
                    }

                    break;

                case "is_fork":
                    if (member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        errors.Add(new ErrorDetail("/ref/is_fork", InvalidValue));
                    }
                    else
                    {
                        isFork = member.Value.GetBoolean();
                    }

                    break;

                default:
                    errors.Add(new ErrorDetail(RequestMemberPath.ForUnknown(member.Name, "/ref"), "additional_properties"));
                    break;
            }
        }

        return errors.Count > 0
            ? (null, errors)
            : (new RunRefInput(kind, name, prNumber, headSha, baseSha, triggerEvent, isFork), errors);
    }

    /// <summary>
    /// The identity a run is created with: the body's values, with every member a verified claim covers replaced by the
    /// claim's (AC1–AC3), and <c>unverified_ref</c> set when a stored member came from the body.
    /// </summary>
    /// <param name="body">The body's validated <c>ref</c> block, or null when the body carried none.</param>
    /// <param name="claim">The verified claim of the caller's <c>ci_token_issue</c> row, or null when the caller has no usable one.</param>
    internal static RunIdentity Resolve(RunRefInput? body, CiRefClaim? claim)
    {
        if (claim is null)
        {
            var kind = body?.Kind;
            var name = body?.Name;
            var prNumber = body?.PrNumber;
            var headSha = body?.HeadSha;
            var baseSha = body?.BaseSha;
            var triggerEvent = body?.TriggerEvent;
            var isFork = body?.IsFork ?? false;

            return new RunIdentity(
                kind,
                name,
                prNumber,
                headSha,
                baseSha,
                triggerEvent,
                isFork,
                kind is not null
                || name is not null
                || prNumber is not null
                || headSha is not null
                || baseSha is not null
                || triggerEvent is not null
                || isFork);
        }

        var (claimKind, claimName, claimPrNumber) = MapClaim(claim.Ref);

        // A claim covers head_sha only for a branch/tag ref; for a pull request the sha is the test-merge commit, and an
        // unparseable ref covers nothing. Either way the body is what the caller has for head_sha, and using it labels
        // the run unverified.
        var claimCoversHead = claimKind is BranchKind or TagKind;
        var bodyHead = claimCoversHead ? null : body?.HeadSha;
        var baseShaFromBody = body?.BaseSha;
        var triggerFromBody = body?.TriggerEvent;

        return new RunIdentity(
            claimKind,
            claimName,
            claimPrNumber,
            claimCoversHead ? ClaimShaOrNull(claim.Sha) : bodyHead,
            baseShaFromBody,
            triggerFromBody,
            IsFork: false,
            UnverifiedRef: bodyHead is not null || baseShaFromBody is not null || triggerFromBody is not null);
    }

    /// <summary>
    /// The one rule that runs on the <em>effective</em> values rather than the body: a <c>pull_request</c> identity
    /// without a number is unusable (AC4). It can only be violated by a body-only caller, because a claim's two members
    /// are produced together.
    /// </summary>
    /// <param name="identity">The resolved identity.</param>
    /// <returns>The failure detail, or null when the identity is admissible.</returns>
    internal static ErrorDetail? EffectiveViolation(RunIdentity identity) =>
        identity.RefKind == PullRequestKind && identity.PrNumber is null
            ? new ErrorDetail("/ref/pr_number", "required")
            : null;

    /// <summary>
    /// Maps a verified <c>ref</c> claim to the three members it covers: <c>refs/heads/&lt;n&gt;</c> to a
    /// <c>branch</c> called <c>&lt;n&gt;</c>, <c>refs/tags/&lt;n&gt;</c> to a <c>tag</c>, and
    /// <c>refs/pull/&lt;N&gt;/merge</c> to a <c>pull_request</c> whose name is GitHub's <c>&lt;N&gt;/merge</c> and whose
    /// number is <c>N</c>. The name is built from the parsed number's canonical decimal, so a ref with leading zeros
    /// (<c>refs/pull/007/merge</c>) stores <c>7/merge</c>, never the raw digits. Any other shape — including a name
    /// over 255 characters, a name with a control character, or a number that does not fit <c>integer</c> — covers
    /// nothing: the three members stay null and the body is still ignored for them.
    /// </summary>
    /// <param name="gitRef">The verified <c>ref</c> claim.</param>
    internal static (string? Kind, string? Name, int? PrNumber) MapClaim(string gitRef)
    {
        if (gitRef.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
        {
            var name = gitRef[BranchRefPrefix.Length..];
            return IsStorableName(name) ? (BranchKind, name, null) : (null, null, null);
        }

        if (gitRef.StartsWith(TagRefPrefix, StringComparison.Ordinal))
        {
            var name = gitRef[TagRefPrefix.Length..];
            return IsStorableName(name) ? (TagKind, name, null) : (null, null, null);
        }

        // The length guard keeps the slice below in range when the prefix and the suffix would overlap
        // (`refs/pull/merge`), which a bare StartsWith/EndsWith pair would not.
        if (gitRef.Length > PullRefPrefix.Length + PullRefSuffix.Length
            && gitRef.StartsWith(PullRefPrefix, StringComparison.Ordinal)
            && gitRef.EndsWith(PullRefSuffix, StringComparison.Ordinal))
        {
            var digits = gitRef[PullRefPrefix.Length..^PullRefSuffix.Length];
            // Digits only, at least one, and a positive number that fits `integer`: anything else cannot be stored in
            // run.pr_number (CHECK pr_number > 0) and must not abort the run with a 22003.
            if (digits.Length > 0
                && IsAsciiDigits(digits)
                && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > 0)
            {
                // The canonical decimal of the parsed number, so `refs/pull/007/merge` stores `7/merge` (the digits
                // slice may carry leading zeros, which are not a name GitHub ever writes).
                return (PullRequestKind, number.ToString(CultureInfo.InvariantCulture) + PullRefSuffix, number);
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// The verified <c>sha</c> claim as a storable <c>head_sha</c>, or null when it is not the
    /// <c>sha</c>-shaped value the column CHECK admits (the exchange does not constrain it).
    /// </summary>
    internal static string? ClaimShaOrNull(string sha) => IsSha(sha) ? sha : null;

    /// <summary>A string member, or false when the JSON value is not a string (null is never returned as a value).</summary>
    private static bool TryString(JsonElement value, out string? text)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString();
            return text is not null;
        }

        text = null;
        return false;
    }

    /// <summary>
    /// A storable <c>ref_name</c>: <c>1..255</c> code points (the <c>run.ref_name</c> CHECK — <c>char_length</c> is the
    /// Postgres semantics the release check uses too) with no control character. A git ref name cannot contain a
    /// C0/C1 control or DEL, and such a value is never a name a caller meant: it is refused with the same
    /// <c>invalid_value</c> reason as any other bad name rather than stored (SEC-F3). Checked on runes, so the DEL and
    /// C1 (the <c>U+0080..U+009F</c> block, <c>U+0085</c> NEL included) characters are covered like <c>\n</c>/<c>\r</c>/<c>\t</c>.
    /// </summary>
    internal static bool IsStorableName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var runes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (++runes > MaxRefNameLength || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c> — GitHub's SHA-1 or SHA-256 commit id, the <c>head_sha</c>/<c>base_sha</c>
    /// CHECK. Lowercase only, and exactly 40 or 64 characters.
    /// </summary>
    internal static bool IsSha(string? sha)
    {
        if (sha is null || sha.Length is not (40 or 64))
        {
            return false;
        }

        foreach (var character in sha)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when every character is an ASCII decimal digit.</summary>
    private static bool IsAsciiDigits(string text)
    {
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

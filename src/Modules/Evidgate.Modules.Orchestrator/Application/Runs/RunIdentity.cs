using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
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
/// The commit the run was built from, as <c>^[0-9a-f]{40}([0-9a-f]{24})?$</c>, as the producer declared it.
/// </param>
/// <param name="BaseSha">The base commit the producer declared.</param>
/// <param name="TriggerEvent">
/// <c>push</c>, <c>pull_request</c>, <c>schedule</c>, <c>manual</c> or <c>dispatch</c>, as the producer declared it.
/// </param>
/// <param name="IsFork">True only when the body says so; a fork run stays advisory-only and never binds to a release.</param>
/// <param name="UnverifiedRef">
/// True when any ref member was supplied: all stored identity values are producer-declared. False without identity.
/// A declared non-fork default-branch or configured release-branch-glob match may gate;
/// declared forks are advisory-only and never bind to a release.
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
/// The run-identity rules of 05.2.2-T1.3 as pure functions, so the trust policy is unit-testable without a database:
/// parse the body's <c>ref</c> block (shape only, AC4) and resolve it to the identity a run stores.
/// <para>
/// Every supplied ref member is producer-declared, so <c>unverified_ref</c> records provenance, not gate eligibility.
/// A declared non-fork default-branch or configured release-branch-glob match may gate;
/// declared forks are advisory-only and never bind to a release.
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

    /// <summary>The <c>ref_kind</c> of a pull-request ref.</summary>
    internal const string PullRequestKind = "pull_request";

    /// <summary>
    /// Parses the body's optional <c>ref</c> object, shape-checking every member (AC4), so a malformed block is
    /// always a 422 before any write.
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
            if (errors.Count > ContractValidationResult.MaxDetails)
            {
                break;
            }

            switch (member.Name)
            {
                case "kind":
                    if (!TryString(member.Value, out kind) || !Kinds.Contains(kind, StringComparer.Ordinal))
                    {
                        AddDetail(errors, new ErrorDetail("/ref/kind", InvalidValue));
                    }

                    break;

                case "name":
                    if (!TryString(member.Value, out name) || !IsStorableName(name))
                    {
                        AddDetail(errors, new ErrorDetail("/ref/name", InvalidValue));
                    }

                    break;

                case "pr_number":
                    if (member.Value.ValueKind != JsonValueKind.Number
                        || !member.Value.TryGetInt32(out var number)
                        || number <= 0)
                    {
                        AddDetail(errors, new ErrorDetail("/ref/pr_number", InvalidValue));
                    }
                    else
                    {
                        prNumber = number;
                    }

                    break;

                case "head_sha":
                    if (!TryString(member.Value, out headSha) || !IsSha(headSha))
                    {
                        AddDetail(errors, new ErrorDetail("/ref/head_sha", InvalidValue));
                    }

                    break;

                case "base_sha":
                    if (!TryString(member.Value, out baseSha) || !IsSha(baseSha))
                    {
                        AddDetail(errors, new ErrorDetail("/ref/base_sha", InvalidValue));
                    }

                    break;

                case "trigger_event":
                    if (!TryString(member.Value, out triggerEvent) || !TriggerEvents.Contains(triggerEvent, StringComparer.Ordinal))
                    {
                        AddDetail(errors, new ErrorDetail("/ref/trigger_event", InvalidValue));
                    }

                    break;

                case "is_fork":
                    if (member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        AddDetail(errors, new ErrorDetail("/ref/is_fork", InvalidValue));
                    }
                    else
                    {
                        isFork = member.Value.GetBoolean();
                    }

                    break;

                default:
                    AddDetail(errors, new ErrorDetail(RequestMemberPath.ForUnknown(member.Name, "/ref"), "additional_properties"));
                    break;
            }
        }

        return errors.Count > 0
            ? (null, errors)
            : (new RunRefInput(kind, name, prNumber, headSha, baseSha, triggerEvent, isFork), errors);
    }

    /// <summary>
    /// Adds one member failure, bounded like every contract refusal: past <see cref="ContractValidationResult.MaxDetails"/>
    /// rows a single terminal <c>truncated</c> marker is added and the caller stops, so a body of repeated or unknown
    /// members cannot be answered with one row each.
    /// </summary>
    private static void AddDetail(List<ErrorDetail> errors, ErrorDetail detail)
    {
        if (errors.Count < ContractValidationResult.MaxDetails)
        {
            errors.Add(detail);
        }
        else if (errors.Count == ContractValidationResult.MaxDetails)
        {
            errors.Add(new ErrorDetail(string.Empty, ContractValidationError.TruncatedReason));
        }
    }

    /// <summary>
    /// The identity a run is created with: the body's declared values, stored as they are, with
    /// <c>unverified_ref</c> set whenever any member was supplied. No credential carries a ref claim, so a
    /// producer's identity is never verified here.
    /// </summary>
    /// <param name="body">The body's validated <c>ref</c> block, or null when the body carried none.</param>
    internal static RunIdentity Resolve(RunRefInput? body)
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

    /// <summary>
    /// The one rule that runs on the <em>effective</em> values rather than the body: a <c>pull_request</c> identity
    /// without a number is unusable (AC4).
    /// </summary>
    /// <param name="identity">The resolved identity.</param>
    /// <returns>The failure detail, or null when the identity is admissible.</returns>
    internal static ErrorDetail? EffectiveViolation(RunIdentity identity) =>
        identity.RefKind == PullRequestKind && identity.PrNumber is null
            ? new ErrorDetail("/ref/pr_number", "required")
            : null;

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
}

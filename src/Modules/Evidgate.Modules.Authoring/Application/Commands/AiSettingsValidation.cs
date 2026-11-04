using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// The request-level validation of a settings save: the document, the template provenance pair and the
/// optional reason, in that order, with the story's reason tokens and JSON pointers.
/// </summary>
/// <remarks>
/// The body reader has already refused a duplicate member, a lone surrogate and U+0000, so this pass only
/// has to decide the values. A template is provenance, never a live reference: its pair must exist, but the
/// document is not required to equal the template's content (an edited copy is the normal case).
/// </remarks>
internal static partial class AiSettingsValidation
{
    /// <summary>The audit metadata outcome of an appended version.</summary>
    internal const string OutcomeCreated = "created";

    /// <summary>The audit metadata outcome of a save whose document equals the head.</summary>
    internal const string OutcomeNoOp = "noop";

    /// <summary>The 422 of a template pair no migration row carries.</summary>
    internal static Error UnknownTemplate => Error.Invalid(
        "the template is not a code and version this platform publishes",
        [new ErrorDetail("/template/code", "not_allowed_value")]);

    /// <summary>
    /// Reads the request and answers the document, its prepared reason and, when the request is refused, the
    /// one 422. A stale <c>If-Match</c> is not decided here: it needs the head, which the lock protects.
    /// </summary>
    /// <param name="command">The save request.</param>
    /// <returns>The accepted document and reason, or the refusal.</returns>
    internal static (AiSettingsDocument? Document, string? Reason, Error? Refusal) Read(SaveAiSettingsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (document, details) = AiSettingsDocument.Parse(command.Settings);
        var refusal = new DetailList(details);

        ReadTemplate(command, refusal);
        var reason = ReadReason(command.Reason, refusal);

        return refusal.Count > 0 || document is null
            ? (null, null, Error.Invalid("the AI settings document is not valid", refusal.ToList()))
            : (document, reason, null);
    }

    /// <summary>
    /// The 412 of a head the caller did not read, carrying the current version so a retry needs no GET.
    /// </summary>
    /// <param name="currentVersion">The head's version, or 0 before the first save.</param>
    internal static Error Stale(int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        "the AI settings changed since they were read; reload the current version and retry",
        412,
        Retryable: false,
        Details: [new ErrorDetail("version", currentVersion.ToString(CultureInfo.InvariantCulture))]);

    /// <summary>
    /// The audit detail of one successful save: ids, version, the before/after digests and the reason, never
    /// any settings text.
    /// </summary>
    /// <param name="settingsId">The settings row the outcome names.</param>
    /// <param name="head">The head before the save, or null for the first version.</param>
    /// <param name="outcome"><see cref="OutcomeCreated"/> or <see cref="OutcomeNoOp"/>.</param>
    /// <param name="versionNo">The version the outcome answers.</param>
    /// <param name="afterHash">The digest of the accepted document.</param>
    /// <param name="reason">The accepted reason, or null.</param>
    internal static AuditObject AuditObject(
        Guid settingsId,
        AiSettingsSql.HeadRow? head,
        string outcome,
        int versionNo,
        string afterHash,
        string? reason)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["outcome"] = outcome,
            ["version_no"] = versionNo.ToString(CultureInfo.InvariantCulture),
        };

        if (AuditReason(reason) is { } audited)
        {
            metadata["reason"] = audited;
        }

        return new AuditObject(
            "project_ai_settings",
            settingsId.ToString("D"),
            BeforeHash: HashBytes(head?.ContentHash),
            AfterHash: HashBytes(afterHash),
            Metadata: metadata);
    }

    /// <summary>The reason as the audit row carries it: bounded, and never longer than a metadata value may be.</summary>
    /// <param name="reason">The accepted reason, or null.</param>
    internal static string? AuditReason(string? reason) =>
        reason is null ? null : AuthoringText.Brief(reason, AuditMetadata.MaxValueLength - 1);

    private static void ReadTemplate(SaveAiSettingsCommand command, DetailList details)
    {
        if (command.TemplateCode is null && command.TemplateVersion is null)
        {
            return;
        }

        if (command.TemplateCode is null)
        {
            details.Add("/template/code", "required");

            return;
        }

        if (command.TemplateVersion is null)
        {
            details.Add("/template/version", "required");

            return;
        }

        if (!TemplateCodeShape().IsMatch(command.TemplateCode))
        {
            details.Add("/template/code", "invalid");
        }

        if (command.TemplateVersion < 1)
        {
            details.Add("/template/version", "invalid");
        }
    }

    private static string? ReadReason(string? reason, DetailList details)
    {
        if (reason is null)
        {
            return null;
        }

        var prepared = AiSettingsText.Prepare(reason);

        if (AiSettingsText.CountCodePoints(prepared) is 0)
        {
            details.Add("/reason", "required");
        }
        else if (AiSettingsText.CountCodePoints(prepared) > AiSettingsText.MaxReasonLength)
        {
            details.Add("/reason", "too_long");
        }
        else if (SecretShape.ValueLooksSecret(prepared))
        {
            details.Add("/reason", "secret_value_detected");
        }
        else
        {
            return prepared;
        }

        return null;
    }

    private static byte[]? HashBytes(string? contentHash) =>
        contentHash is null ? null : Convert.FromHexString(contentHash[CanonicalHash.Prefix.Length..]);

    /// <summary>The ordered, bounded collector that merges the document's own details with the request's.</summary>
    private sealed class DetailList(IReadOnlyList<ErrorDetail> initial)
    {
        private readonly List<ErrorDetail> _details = [.. initial];

        internal int Count => _details.Count;

        internal void Add(string path, string reason)
        {
            if (_details.Count < AiSettingsDocument.MaxDetails)
            {
                _details.Add(new ErrorDetail(path, reason));
            }
        }

        internal List<ErrorDetail> ToList() => _details;
    }

    /// <summary>The template code shape the column CHECK enforces: <c>^[a-z][a-z0-9_-]{1,31}$</c>.</summary>
    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{1,31}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TemplateCodeShape();
}

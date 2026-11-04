using System.Text.Json;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The wire shapes of the project AI settings routes and the one mapping from a stored row to
/// them, so the five handlers answer the same member names and the same document in every response.
/// </summary>
/// <remarks>
/// A version is <b>the stored document</b>: text is already composed and trimmed, the languages array is
/// canonical, and the hash is the digest of those eight members. The default document is the platform
/// template's stored content and its stored digest, unchanged, so a client that recomputes the hash from
/// the answer gets the same string.
/// </remarks>
internal static class AiSettingsSources
{
    /// <summary>A project's own saved version.</summary>
    internal const string Saved = "saved";

    /// <summary>The platform default template, before a project saved anything.</summary>
    internal const string DefaultTemplate = "default_template";
}

/// <summary>The effective document of a project: a saved head, or the default template as version 0.</summary>
/// <param name="VersionNo">The version number; 0 before the first save.</param>
/// <param name="Source"><see cref="AiSettingsSources.Saved"/> or <see cref="AiSettingsSources.DefaultTemplate"/>.</param>
/// <param name="SettingsId">The settings row's id, or null for the default.</param>
/// <param name="ContentHash">The digest the document is addressed by.</param>
/// <param name="Settings">The document itself.</param>
/// <param name="Template">The template provenance, or the default template's pair.</param>
/// <param name="Author">The membership that saved it, or null for the default.</param>
/// <param name="Reason">The save reason, or null.</param>
/// <param name="CreatedAt">When it was appended, or null for the default.</param>
internal sealed record AiSettingsView(
    int VersionNo,
    string Source,
    Guid? SettingsId,
    string ContentHash,
    AiSettingsDocument Settings,
    AiSettingsTemplateRef? Template,
    Guid? Author,
    string? Reason,
    DateTimeOffset? CreatedAt);

/// <summary>A template <c>(code, version)</c> pair recorded as provenance, never as a live reference.</summary>
/// <param name="Code">The template's code.</param>
/// <param name="Version">The template's version at the time of the copy.</param>
internal sealed record AiSettingsTemplateRef(string Code, int Version);

/// <summary>One version in the timeline: metadata only, no document.</summary>
/// <param name="VersionNo">The version number.</param>
/// <param name="ContentHash">The canonical digest.</param>
/// <param name="Template">The provenance pair, or null.</param>
/// <param name="AuthorMembershipId">The membership that saved it.</param>
/// <param name="Reason">The save reason, or null.</param>
/// <param name="CreatedAt">When it was appended.</param>
internal sealed record AiSettingsVersionSummary(
    int VersionNo,
    string ContentHash,
    AiSettingsTemplateRef? Template,
    Guid AuthorMembershipId,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>A keyset page of version summaries.</summary>
/// <param name="Items">The page, newest first.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null on the last page.</param>
internal sealed record AiSettingsVersionListResponse(
    IReadOnlyList<AiSettingsVersionSummary> Items,
    string? NextCursor);

/// <summary>One version with its stored document and metadata.</summary>
/// <param name="VersionNo">The version number.</param>
/// <param name="SettingsId">The settings row's id.</param>
/// <param name="ContentHash">The canonical digest.</param>
/// <param name="Settings">The stored document.</param>
/// <param name="Template">The provenance pair, or null.</param>
/// <param name="AuthorMembershipId">The membership that saved it.</param>
/// <param name="Reason">The save reason, or null.</param>
/// <param name="CreatedAt">When it was appended.</param>
internal sealed record AiSettingsVersion(
    int VersionNo,
    Guid SettingsId,
    string ContentHash,
    AiSettingsDocument Settings,
    AiSettingsTemplateRef? Template,
    Guid AuthorMembershipId,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>One platform template with its document.</summary>
/// <param name="Code">The template's code.</param>
/// <param name="Version">The template's version.</param>
/// <param name="Locale"><c>vi</c> or <c>en</c>.</param>
/// <param name="Name">The display name.</param>
/// <param name="IsDefault">Whether this is the locale's default.</param>
/// <param name="ContentHash">The stored digest of the document.</param>
/// <param name="Settings">The stored document.</param>
internal sealed record AiSettingsTemplateView(
    string Code,
    int Version,
    string Locale,
    string Name,
    bool IsDefault,
    string ContentHash,
    AiSettingsDocument Settings);

/// <summary>The closed template catalogue. <paramref name="NextCursor"/> is always null: it is not paged.</summary>
/// <param name="Items">Every template, ordered by code, locale and version.</param>
/// <param name="NextCursor">Always null.</param>
internal sealed record AiSettingsTemplateListResponse(
    IReadOnlyList<AiSettingsTemplateView> Items,
    string? NextCursor);

/// <summary>The row-to-wire mapping shared by the four read handlers and the save's 200/201 answers.</summary>
internal static class AiSettingsModels
{
    /// <summary>The effective view: the saved head when there is one, else the default template as version 0.</summary>
    /// <param name="row">The effective-document row.</param>
    internal static AiSettingsView Effective(AiSettingsSql.EffectiveRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.SettingsId is { } settingsId)
        {
            return new AiSettingsView(
                row.VersionNo ?? 0,
                AiSettingsSources.Saved,
                settingsId,
                row.ContentHash ?? throw new InvalidOperationException("a saved settings row has no content hash"),
                Document(row.Description, row.EnvironmentUrl, row.Languages, row.WritingStyle, row.CoverageStrategy,
                    row.Critical, row.High, row.Medium, row.Low, row.ExclusionRules, row.StepDetail),
                TemplateRef(row.TemplateCode, row.TemplateVersion),
                row.AuthorMembershipId,
                row.Reason,
                row.CreatedAt);
        }

        if (row.DefaultContent is null || row.DefaultContentHash is null || row.DefaultCode is null || row.DefaultVersion is null)
        {
            throw new InvalidOperationException("a project without settings has no default ai settings template");
        }

        return new AiSettingsView(
            0,
            AiSettingsSources.DefaultTemplate,
            null,
            row.DefaultContentHash,
            Stored(row.DefaultContent),
            new AiSettingsTemplateRef(row.DefaultCode, row.DefaultVersion.Value),
            null,
            null,
            null);
    }

    /// <summary>One version with its document.</summary>
    /// <param name="row">The stored row.</param>
    internal static AiSettingsVersion Version(AiSettingsSql.VersionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AiSettingsVersion(
            row.VersionNo,
            row.SettingsId,
            row.ContentHash,
            Document(row.Description, row.EnvironmentUrl, row.Languages, row.WritingStyle, row.CoverageStrategy,
                row.Critical, row.High, row.Medium, row.Low, row.ExclusionRules, row.StepDetail),
            TemplateRef(row.TemplateCode, row.TemplateVersion),
            row.AuthorMembershipId,
            row.Reason,
            row.CreatedAt);
    }

    /// <summary>One timeline summary.</summary>
    /// <param name="row">The stored row.</param>
    internal static AiSettingsVersionSummary Summary(AiSettingsSql.VersionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AiSettingsVersionSummary(
            row.VersionNo,
            row.ContentHash,
            TemplateRef(row.TemplateCode, row.TemplateVersion),
            row.AuthorMembershipId,
            row.Reason,
            row.CreatedAt);
    }

    /// <summary>One template row with its stored document.</summary>
    /// <param name="row">The stored row.</param>
    internal static AiSettingsTemplateView Template(AiSettingsSql.TemplateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AiSettingsTemplateView(
            row.Code,
            row.Version,
            row.Locale,
            row.Name,
            row.IsDefault,
            row.ContentHash,
            Stored(row.Content));
    }

    /// <summary>The provenance pair, or null when the row records none.</summary>
    /// <param name="code">The stored code.</param>
    /// <param name="version">The stored version.</param>
    internal static AiSettingsTemplateRef? TemplateRef(string? code, int? version) =>
        code is null || version is null ? null : new AiSettingsTemplateRef(code, version.Value);

    /// <summary>The document of a saved row, built from its typed columns.</summary>
    private static AiSettingsDocument Document(
        string? description,
        string? environmentUrl,
        string[]? languages,
        string? writingStyle,
        string? coverageStrategy,
        string? critical,
        string? high,
        string? medium,
        string? low,
        string? exclusionRules,
        string? stepDetail) =>
        new(
            description ?? string.Empty,
            environmentUrl,
            languages ?? [],
            writingStyle ?? string.Empty,
            coverageStrategy ?? string.Empty,
            new AiSettingsPriorityCriteria(critical ?? string.Empty, high ?? string.Empty, medium ?? string.Empty, low ?? string.Empty),
            exclusionRules ?? string.Empty,
            stepDetail ?? string.Empty);

    /// <summary>The document of a stored template, read from its <c>content</c> JSON.</summary>
    private static AiSettingsDocument Stored(string content)
    {
        using var document = JsonDocument.Parse(content);

        return AiSettingsDocument.FromStored(document.RootElement);
    }
}

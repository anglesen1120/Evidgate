namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements and Dapper rows of the project AI settings: one append-only version chain per
/// project, the per-project advisory lock that serialises its writers, and the read-only projections the
/// four read routes answer from.
/// </summary>
/// <remarks>
/// Every statement carries <c>org_id</c> and the project of the URL, so another organization's or another
/// project's row matches nothing and the route answers 404 without a second lookup. The head and the list are
/// both one backward scan of <c>project_ai_settings_head_idx</c> (<c>version_no desc</c>); the list is a keyset
/// page on that one column, never an offset. The template catalogue is read-only and small, so its route
/// issues one ordered select and no cursor.
/// </remarks>
internal static class AiSettingsSql
{
    /// <summary>
    /// The per-project writer lock: transaction-scoped, so two saves of one project run one after the other
    /// and the second reads the head the first committed. A row lock on the head would not serialise them —
    /// the chain is append-only and both writers would lock the same old row nobody updates.
    /// </summary>
    internal const string LockProject = """
        select pg_advisory_xact_lock(hashtextextended(@lock_key, 0))
        """;

    /// <summary>The head of the chain: the highest version, or no row before the first save.</summary>
    internal const string Head = """
        select v.id, v.version_no, v.content_hash, v.template_code, v.template_version,
               v.author_membership_id, v.reason, v.created_at
          from public.project_ai_settings v
         where v.org_id = @org_id
           and v.project_id = @project_id
         order by v.version_no desc
         limit 1
        """;

    /// <summary>Whether a template <c>(code, version)</c> pair exists at all, in either locale.</summary>
    internal const string TemplateExists = """
        select 1
          from public.ai_settings_template
         where code = @code
           and version = @version
         limit 1
        """;

    /// <summary>
    /// The append of one version. <c>on conflict do nothing</c> is the last line of defence behind the
    /// advisory lock: a lost race inserts nothing and the command answers the stale precondition.
    /// </summary>
    internal const string InsertVersion = """
        insert into public.project_ai_settings (
            id, org_id, project_id, version_no, description, environment_url, languages, writing_style,
            coverage_strategy, priority_criterion_critical, priority_criterion_high, priority_criterion_medium,
            priority_criterion_low, exclusion_rules, step_detail, template_code, template_version, content_hash,
            author_membership_id, reason)
        values (
            @id, @org_id, @project_id, @version_no, @description, @environment_url, @languages, @writing_style,
            @coverage_strategy, @critical, @high, @medium, @low, @exclusion_rules, @step_detail, @template_code,
            @template_version, @content_hash, @author_membership_id, @reason)
        on conflict (org_id, project_id, version_no) do nothing
        returning id, created_at
        """;

    /// <summary>
    /// The effective document of a project: its head row, or — when it never saved — the platform default
    /// template (the <c>en</c> <c>default</c> row, chosen by <c>is_default</c>, never by request locale).
    /// The project's liveness is part of the same statement, so an unknown project is one 404.
    /// </summary>
    internal const string Effective = """
        select s.id as settings_id, s.version_no, s.content_hash, s.description, s.environment_url, s.languages,
               s.writing_style, s.coverage_strategy, s.priority_criterion_critical as critical,
               s.priority_criterion_high as high, s.priority_criterion_medium as medium,
               s.priority_criterion_low as low, s.exclusion_rules, s.step_detail,
               s.template_code, s.template_version, s.author_membership_id, s.reason, s.created_at,
               t.code as default_code, t.version as default_version, t.content::text as default_content,
               t.content_hash as default_content_hash
          from public.project p
          left join lateral (
              select x.*
                from public.project_ai_settings x
               where x.org_id = p.org_id
                 and x.project_id = p.id
               order by x.version_no desc
               limit 1) s on true
          left join lateral (
              select x.code, x.version, x.content, x.content_hash
                from public.ai_settings_template x
               where x.is_default
                 and x.locale = 'en'
               order by x.version desc
               limit 1) t on s.id is null
         where p.org_id = @org_id
           and p.id = @project_id
           and p.status <> 'deleted'
        """;

    /// <summary>The projection of one version summary: no document, the screen fetches two versions to diff.</summary>
    private const string SummaryColumns = """
        v.version_no, v.content_hash, v.template_code, v.template_version,
        v.author_membership_id, v.reason, v.created_at
        """;

    /// <summary>The first page of the version list: the head index's order, no seek.</summary>
    internal const string FirstPage = $"""
        select {SummaryColumns}
          from public.project_ai_settings v
         where v.org_id = @org_id
           and v.project_id = @project_id
         order by v.version_no desc
         limit @take
        """;

    /// <summary>
    /// A later page: the same order, narrowed by the version number the previous page stopped at. The version
    /// number is the chain's own key — unique per project, monotone and never rewritten — so one column is a
    /// complete keyset and no row is skipped or repeated at a page boundary.
    /// </summary>
    internal const string NextPage = $"""
        select {SummaryColumns}
          from public.project_ai_settings v
         where v.org_id = @org_id
           and v.project_id = @project_id
           and v.version_no < @cursor_version_no
         order by v.version_no desc
         limit @take
        """;

    /// <summary>One version with its document and metadata; the project's liveness is part of the statement.</summary>
    internal const string VersionDetail = """
        select v.id as settings_id, v.version_no, v.content_hash, v.description, v.environment_url, v.languages,
               v.writing_style, v.coverage_strategy, v.priority_criterion_critical as critical,
               v.priority_criterion_high as high, v.priority_criterion_medium as medium,
               v.priority_criterion_low as low, v.exclusion_rules, v.step_detail,
               v.template_code, v.template_version, v.author_membership_id, v.reason, v.created_at
          from public.project_ai_settings v
         where v.org_id = @org_id
           and v.project_id = @project_id
           and v.version_no = @version_no
           and exists (select 1
                         from public.project p
                        where p.org_id = @org_id
                          and p.id = @project_id
                          and p.status <> 'deleted')
        """;

    /// <summary>The whole platform template catalogue: closed and small, ordered for a stable answer.</summary>
    internal const string Templates = """
        select t.code, t.version, t.locale, t.name, t.is_default, t.content_hash, t.content::text as content
          from public.ai_settings_template t
         order by t.code, t.locale, t.version
        """;

    /// <summary>The head row as the save command reads it, before deciding 412, 200 or an insert.</summary>
    internal sealed class HeadRow
    {
        /// <summary>The version row's id.</summary>
        public Guid Id { get; init; }

        /// <summary>The highest version.</summary>
        public int VersionNo { get; init; }

        /// <summary>The canonical digest of that version's document.</summary>
        public string ContentHash { get; init; } = string.Empty;

        /// <summary>The template the version was copied from, or null.</summary>
        public string? TemplateCode { get; init; }

        /// <summary>The template's version, or null.</summary>
        public int? TemplateVersion { get; init; }

        /// <summary>The membership that saved it.</summary>
        public Guid AuthorMembershipId { get; init; }

        /// <summary>The caller's reason, or null.</summary>
        public string? Reason { get; init; }

        /// <summary>When the row was appended.</summary>
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>The effective-document row: a saved head, or the default template when the head columns are null.</summary>
    internal sealed class EffectiveRow
    {
        /// <summary>The saved settings row's id, or null for the default template.</summary>
        public Guid? SettingsId { get; init; }

        /// <summary>The saved version, or null for the default template.</summary>
        public int? VersionNo { get; init; }

        /// <summary>The saved document's hash, or null.</summary>
        public string? ContentHash { get; init; }

        /// <summary>The description column.</summary>
        public string? Description { get; init; }

        /// <summary>The environment URL column.</summary>
        public string? EnvironmentUrl { get; init; }

        /// <summary>The languages column.</summary>
        public string[]? Languages { get; init; }

        /// <summary>The writing-style column.</summary>
        public string? WritingStyle { get; init; }

        /// <summary>The coverage-strategy column.</summary>
        public string? CoverageStrategy { get; init; }

        /// <summary>The critical criterion column.</summary>
        public string? Critical { get; init; }

        /// <summary>The high criterion column.</summary>
        public string? High { get; init; }

        /// <summary>The medium criterion column.</summary>
        public string? Medium { get; init; }

        /// <summary>The low criterion column.</summary>
        public string? Low { get; init; }

        /// <summary>The exclusion-rules column.</summary>
        public string? ExclusionRules { get; init; }

        /// <summary>The step-detail column.</summary>
        public string? StepDetail { get; init; }

        /// <summary>The template provenance code, or null.</summary>
        public string? TemplateCode { get; init; }

        /// <summary>The template provenance version, or null.</summary>
        public int? TemplateVersion { get; init; }

        /// <summary>The author membership, or null.</summary>
        public Guid? AuthorMembershipId { get; init; }

        /// <summary>The reason, or null.</summary>
        public string? Reason { get; init; }

        /// <summary>When the row was appended, or null for the default template.</summary>
        public DateTimeOffset? CreatedAt { get; init; }

        /// <summary>The default template's code.</summary>
        public string? DefaultCode { get; init; }

        /// <summary>The default template's version.</summary>
        public int? DefaultVersion { get; init; }

        /// <summary>The default template's stored document.</summary>
        public string? DefaultContent { get; init; }

        /// <summary>The default template's stored digest.</summary>
        public string? DefaultContentHash { get; init; }
    }

    /// <summary>One version row of the version chain, with or without its document.</summary>
    internal sealed class VersionRow
    {
        /// <summary>The settings row's id.</summary>
        public Guid SettingsId { get; init; }

        /// <summary>The version number.</summary>
        public int VersionNo { get; init; }

        /// <summary>The canonical digest.</summary>
        public string ContentHash { get; init; } = string.Empty;

        /// <summary>The document's description.</summary>
        public string? Description { get; init; }

        /// <summary>The document's environment URL.</summary>
        public string? EnvironmentUrl { get; init; }

        /// <summary>The document's languages.</summary>
        public string[]? Languages { get; init; }

        /// <summary>The document's writing style.</summary>
        public string? WritingStyle { get; init; }

        /// <summary>The document's coverage strategy.</summary>
        public string? CoverageStrategy { get; init; }

        /// <summary>The critical criterion.</summary>
        public string? Critical { get; init; }

        /// <summary>The high criterion.</summary>
        public string? High { get; init; }

        /// <summary>The medium criterion.</summary>
        public string? Medium { get; init; }

        /// <summary>The low criterion.</summary>
        public string? Low { get; init; }

        /// <summary>The exclusion rules.</summary>
        public string? ExclusionRules { get; init; }

        /// <summary>The step detail.</summary>
        public string? StepDetail { get; init; }

        /// <summary>The template provenance code, or null.</summary>
        public string? TemplateCode { get; init; }

        /// <summary>The template provenance version, or null.</summary>
        public int? TemplateVersion { get; init; }

        /// <summary>The author membership.</summary>
        public Guid AuthorMembershipId { get; init; }

        /// <summary>The reason, or null.</summary>
        public string? Reason { get; init; }

        /// <summary>When the row was appended.</summary>
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>One platform template row.</summary>
    internal sealed class TemplateRow
    {
        /// <summary>The template's code.</summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>The template's version.</summary>
        public int Version { get; init; }

        /// <summary><c>vi</c> or <c>en</c>.</summary>
        public string Locale { get; init; } = string.Empty;

        /// <summary>The display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Whether this is the locale's default template.</summary>
        public bool IsDefault { get; init; }

        /// <summary>The stored digest of the document.</summary>
        public string ContentHash { get; init; } = string.Empty;

        /// <summary>The stored document as JSON text.</summary>
        public string Content { get; init; } = string.Empty;
    }
}

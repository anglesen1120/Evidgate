using System.Text.Json;
using Evidgate.Modules.Catalog.Contracts;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>The Dapper statements of the draft side, one statement per handler step. RLS scopes every row to the tenant.</summary>
internal static class DraftReadSql
{
    private const string DraftColumns = """
               d.id, d.project_id, d.author_membership_id, d.kind, d.base_commit, d.status, d.version,
               d.content::text as content, d.provenance::text as provenance, d.ai_run_id, d.dry_run_id,
               d.allow_unverified, d.unverified_reason, d.pr_branch, d.pr_commit_sha, d.pr_url, d.pr_number,
               d.linked_case_id, d.linked_revision_id, d.created_at, d.updated_at
        """;

    /// <summary>One draft by id; another tenant's id matches no row, so it answers the same 404 as a missing one.</summary>
    internal const string DraftById = $"""
        select {DraftColumns}
          from public.draft d
         where d.id = @draft_id
        """;

    /// <summary>First page of a project's drafts on <c>draft_project_list</c>; an optional status filter is the second index.</summary>
    internal const string DraftsFirstPage = $"""
        select {DraftColumns}
          from public.draft d
         where d.org_id = @org_id
           and d.project_id = @project_id
           and (@status::text is null or d.status = @status)
         order by d.created_at,
                  d.id
         limit @limit
        """;

    internal const string DraftsNextPage = $"""
        select {DraftColumns}
          from public.draft d
         where d.org_id = @org_id
           and d.project_id = @project_id
           and (@status::text is null or d.status = @status)
           and (d.created_at, d.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by d.created_at,
                  d.id
         limit @limit
        """;

    /// <summary>The caller's active membership of the tenant, or no row: a non-member cannot author a draft.</summary>
    internal const string ActorMembership = """
        select m.id
          from public.membership m
         where m.org_id = @org_id
           and m.user_id = @user_id
           and m.status = 'active'
         order by m.id
         limit 1
        """;
}

/// <summary>One row of <see cref="DraftReadSql"/>: the draft with its JSON columns still as text.</summary>
internal sealed record DraftRow(
    Guid Id,
    Guid ProjectId,
    Guid AuthorMembershipId,
    string Kind,
    string BaseCommit,
    string Status,
    int Version,
    string Content,
    string? Provenance,
    Guid? AiRunId,
    Guid? DryRunId,
    bool AllowUnverified,
    string? UnverifiedReason,
    string? PrBranch,
    string? PrCommitSha,
    string? PrUrl,
    int? PrNumber,
    Guid? LinkedCaseId,
    Guid? LinkedRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The wire shape of the row.</summary>
    internal DraftDto ToDto() => new(
        Id,
        ProjectId,
        AuthorMembershipId,
        Kind,
        BaseCommit,
        Status,
        Version,
        DraftJson.ReadContent(Content),
        DraftJson.ReadObject(Provenance),
        AiRunId,
        DryRunId,
        AllowUnverified,
        UnverifiedReason,
        PrBranch,
        PrCommitSha,
        PrUrl,
        PrNumber,
        LinkedCaseId,
        LinkedRevisionId,
        CreatedAt,
        UpdatedAt);
}

/// <summary>The one serialiser shape of a draft's JSON columns, shared by the write path and the read projection.</summary>
internal static class DraftJson
{
    /// <summary>Snake-case wire names, so the stored <c>content</c> uses the same names as the API.</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>The content set of a stored row. A row this module wrote always parses; anything else is a bug.</summary>
    internal static DraftContentDto ReadContent(string content) =>
        JsonSerializer.Deserialize<DraftContentDto>(content, Options)
        ?? throw new InvalidOperationException("a stored draft carries no content set");

    /// <summary>A stored provenance object as an element, or null.</summary>
    internal static JsonElement? ReadObject(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

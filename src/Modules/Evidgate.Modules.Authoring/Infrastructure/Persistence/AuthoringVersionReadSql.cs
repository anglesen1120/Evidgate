using System.Text.Json;
using Evidgate.Modules.Authoring.Application.Queries;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements and Dapper rows of the authoring version read model (13.1.1 §5 routes 22 and 23): one keyset page of
/// <c>public.authoring_version</c> newest-first, and one stored version with its <c>content</c> passed through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, append-only.</b> This file has no <c>insert</c>, <c>update</c> or <c>delete</c>: every version row is
/// appended by <see cref="Application.AuthoringVersionWriter"/> and nothing in the module modifies one afterwards
/// (AC5/AC6). Both statements run on the scope's connection inside the read-only transaction the C2 pipeline opens for
/// an <c>IQuery</c> (C1/C5), and both carry <c>org_id</c> <em>and</em> the project of the URL, so a version of another
/// project of the same organization, another organization's version or a random id are all one 404 (AC10).
/// </para>
/// <para>
/// <b>Three complete statements, two declared indexes.</b> <c>authoring_version_timeline_idx</c>
/// (<c>org_id, project_id, created_at desc, id desc</c>, 0039:391-392) serves the newest-first order of AC9;
/// <c>authoring_version_entity_key</c> (0039:372-373) serves a per-entity history. The filter predicates are written as
/// <c>@filter is null or column = @filter</c> (the <c>AuthoringManualCaseReadSql</c> shape), which PostgreSQL evaluates
/// as a <b>residual filter</b> — correct but not searchable — so the two shapes whose equality must be an index
/// condition get a statement of their own, and the choice between the three happens in C# over fixed constants
/// (<see cref="AuthoringVersionStatement.Pick"/>), never by assembling SQL text from the request (00 §5.7 S1, the
/// OI-13-112 rule of <c>AuthoringNodeReadSql</c>).
/// </para>
/// <para>
/// <b>Keyset, microsecond-exact.</b> The cursor is the <c>(created_at, id)</c> pair of the page's last row and the seek
/// is always present: the first page passes the sentinel pair
/// <see cref="AuthoringVersionCursor.FirstPageTimestamp"/> / <see cref="AuthoringVersionCursor.FirstPageId"/>, so a first
/// page and a later page are the <b>same</b> statement and the seek is one searchable row comparison. A second,
/// <c>is null or …</c> form for the first page would be a filter, and every later page would rescan the project from its
/// newest row. <c>created_at</c> is <c>timestamptz</c> (microseconds) and the parameter is a
/// <see cref="DateTimeOffset"/>, which Npgsql binds as <c>timestamptz</c>, so no row is skipped or repeated at a page
/// boundary even when several rows share one <c>created_at</c>.
/// </para>
/// <para>
/// The rows are mutable classes with settable properties: Dapper binds them through
/// <c>DefaultTypeMap.MatchNamesWithUnderscores</c>, and a positional record cannot bind a projection where one column
/// (<c>content</c>) exists only in the detail statement.
/// </para>
/// </remarks>
internal static class AuthoringVersionReadSql
{
    /// <summary>
    /// The project probe of both routes: another organization's project, a soft-deleted project and a project that never
    /// existed answer the same 404 (AC10). The tenant is made explicit rather than left to row level security, so an
    /// unset tenant setting answers "no project" instead of every project.
    /// </summary>
    internal const string ProjectExists = """
        select 1
          from public.project p
         where p.org_id = @org_id
           and p.id = @project_id
           and p.status <> 'deleted'
        """;

    /// <summary>The projection of one timeline row: the twelve members of §5 route 22, and no <c>content</c>.</summary>
    private const string Columns = """
        v.id as version_id, v.entity_kind, v.entity_id, v.version_no, v.batch_id, v.change_kind,
        v.content_hash, v.source, v.author_membership_id as author, v.reason, v.ai_run_id, v.created_at
        """;

    /// <summary>
    /// The timeline shape of AC9: no <c>entity_kind</c> filter, so the <c>(created_at desc, id desc)</c> order is served
    /// by <c>authoring_version_timeline_idx</c> and every filter is residual. <c>@entity_kind</c> is bound but always null
    /// here, so all three statements take one parameter set. There is no <c>@entity_id</c> predicate: the handler refuses
    /// <c>entity_id</c> without <c>entity_kind</c>, so no request can reach this shape with an entity id (<see
    /// cref="EntityPage"/> serves the pair) and the statement carries no unreachable entity-id branch. The range bounds
    /// are <c>coalesce(@from_at, '-infinity')</c>/<c>coalesce(@to_at, 'infinity')</c> on purpose: an <c>is null or</c>
    /// form would drop the range out of the index condition, while an absent bound as infinity keeps <c>created_at</c>
    /// searchable.
    /// </summary>
    internal const string TimelinePage = $"""
        select {Columns}
          from public.authoring_version v
         where v.org_id = @org_id
           and v.project_id = @project_id
           and (v.created_at, v.id) < (@cursor_created_at::timestamptz, @cursor_id::uuid)
           and (@entity_kind::text is null or v.entity_kind = @entity_kind)
           and (@source::text is null or v.source = @source)
           and (@actor::uuid is null or v.author_membership_id = @actor)
           and v.created_at >= coalesce(@from_at::timestamptz, '-infinity'::timestamptz)
           and v.created_at < coalesce(@to_at::timestamptz, 'infinity'::timestamptz)
         order by v.created_at desc,
                  v.id desc
         limit @take
        """;

    /// <summary>
    /// The per-entity-kind shape: <c>entity_kind</c> is an index condition on
    /// <c>authoring_version_entity_key</c> (<c>org_id, project_id, entity_kind, entity_id, version_no</c>) instead of a
    /// filter. <c>@entity_id</c> is bound but always null here — the handler refuses the id without a kind and picks
    /// <see cref="EntityPage"/> once the pair is present — so the parameter set stays shared, exactly as it does for
    /// <c>@entity_kind</c> in <see cref="TimelinePage"/>.
    /// </summary>
    internal const string EntityKindPage = $"""
        select {Columns}
          from public.authoring_version v
         where v.org_id = @org_id
           and v.project_id = @project_id
           and (v.created_at, v.id) < (@cursor_created_at::timestamptz, @cursor_id::uuid)
           and v.entity_kind = @entity_kind
           and (@entity_id::uuid is null or v.entity_id = @entity_id)
           and (@source::text is null or v.source = @source)
           and (@actor::uuid is null or v.author_membership_id = @actor)
           and v.created_at >= coalesce(@from_at::timestamptz, '-infinity'::timestamptz)
           and v.created_at < coalesce(@to_at::timestamptz, 'infinity'::timestamptz)
         order by v.created_at desc,
                  v.id desc
         limit @take
        """;

    /// <summary>
    /// The per-entity history shape: both halves of <c>authoring_version_entity_key</c>'s entity prefix are index
    /// conditions, so one entity's history is a short index range even when the project holds a long timeline.
    /// </summary>
    internal const string EntityPage = $"""
        select {Columns}
          from public.authoring_version v
         where v.org_id = @org_id
           and v.project_id = @project_id
           and (v.created_at, v.id) < (@cursor_created_at::timestamptz, @cursor_id::uuid)
           and v.entity_kind = @entity_kind
           and v.entity_id = @entity_id
           and (@source::text is null or v.source = @source)
           and (@actor::uuid is null or v.author_membership_id = @actor)
           and v.created_at >= coalesce(@from_at::timestamptz, '-infinity'::timestamptz)
           and v.created_at < coalesce(@to_at::timestamptz, 'infinity'::timestamptz)
         order by v.created_at desc,
                  v.id desc
         limit @take
        """;

    /// <summary>
    /// One stored version with its <c>content</c> (route 23). The content leaves the database as JSON text
    /// (<c>content::text</c>) and reaches the client as the stored document, never through a typed model, so the caller
    /// sees exactly what the writer hashed (AC9). The project's liveness is part of the same statement (AC10): a
    /// soft-deleted project answers the same 404 its list route answers, with no second round trip, and the id of another
    /// project's version simply matches no row.
    /// </summary>
    internal const string VersionDetail = $"""
        select {Columns},
               v.content::text as content
          from public.authoring_version v
         where v.org_id = @org_id
           and v.project_id = @project_id
           and v.id = @version_id
           and exists (select 1
                         from public.project p
                        where p.id = v.project_id
                          and p.org_id = v.org_id
                          and p.status <> 'deleted')
        """;
}

/// <summary>
/// One row of a <c>GET …/authoring/versions</c> page (route 22) and, for route 23, the same row plus its stored
/// <c>content</c>.
/// </summary>
internal sealed class AuthoringVersionRow
{
    /// <summary>The <c>authoring_version.id</c>.</summary>
    public Guid VersionId { get; set; }

    /// <summary><c>feature_node</c>, <c>checklist_item</c> or <c>manual_test_case</c>.</summary>
    public string EntityKind { get; set; } = string.Empty;

    /// <summary>The entity the version describes.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The entity's version number at the time of the change.</summary>
    public int VersionNo { get; set; }

    /// <summary>The change this row belongs to: every row one command appended shares it.</summary>
    public Guid BatchId { get; set; }

    /// <summary><c>create</c>, <c>update</c>, <c>delete</c>, <c>restore</c> or <c>import</c>.</summary>
    public string ChangeKind { get; set; } = string.Empty;

    /// <summary><c>manual</c>, <c>ai</c> or <c>import</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The RFC 8785 digest of the stored <c>content</c> (AC5/AC14).</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The authoring membership that wrote the change, or null for a principal without one.</summary>
    public Guid? Author { get; set; }

    /// <summary>The caller's reason, when the change carried one.</summary>
    public string? Reason { get; set; }

    /// <summary>The generation run that produced the change, when it was an AI change.</summary>
    public Guid? AiRunId { get; set; }

    /// <summary>When the row was appended, to the microsecond.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The stored <c>{"…"}</c> document as JSON text (route 23 only; <c>content::text</c>, so Dapper never sees jsonb).
    /// </summary>
    public string? Content { get; set; }

    /// <summary>The row as the wire type of route 22 — never the content.</summary>
    public AuthoringVersionListItemDto ToListItem() => new(
        VersionId,
        EntityKind,
        EntityId,
        VersionNo,
        BatchId,
        ChangeKind,
        Source,
        ContentHash,
        Author,
        Reason,
        AiRunId,
        CreatedAt);

    /// <summary>
    /// The row as the wire type of route 23: the same members plus the stored document, passed through as the JSON the
    /// writer hashed rather than re-serialised through a typed model (AC9). The column is a validated <c>jsonb</c>
    /// object, so a value that no longer parses (a hand-edited row) answers an empty object instead of a 500.
    /// </summary>
    public AuthoringVersionDetailDto ToDetail()
    {
        using var document = Parse(Content);

        return new AuthoringVersionDetailDto(
            VersionId,
            EntityKind,
            EntityId,
            VersionNo,
            BatchId,
            ChangeKind,
            Source,
            ContentHash,
            Author,
            Reason,
            AiRunId,
            CreatedAt,
            document.RootElement.Clone());
    }

    /// <summary>The stored document, or an empty object for a row whose text no longer parses.</summary>
    /// <param name="content">The row's <c>content</c> text.</param>
    private static JsonDocument Parse(string? content)
    {
        if (content is not null)
        {
            try
            {
                return JsonDocument.Parse(content);
            }
            catch (JsonException)
            {
                // Fall through to the empty object: the column is jsonb, so this only guards a hand-edited row.
            }
        }

        return JsonDocument.Parse("{}");
    }
}

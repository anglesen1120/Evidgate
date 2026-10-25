using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// One keyset page of a project's version timeline (13.1.1 §5 route 22, AC9): newest first, every row carrying the
/// <c>batch_id</c> one command wrote, and never a <c>content</c>.
/// </summary>
/// <remarks>
/// The <c>actor</c> member of the §5 table is spelled <see cref="ActorMembershipId"/> here while AC9 calls the filter
/// <c>actor_membership_id</c>; the route binds §5's spelling and the record member is not on the wire, so only the query
/// parameter name is a contract. Recorded as a [DECISION] with the slice's hand-over.
/// </remarks>
/// <param name="ProjectId">The project from the route; another tenant's project is a 404 (AC10).</param>
/// <param name="EntityKind">Optional filter: <c>feature_node</c>, <c>checklist_item</c> or <c>manual_test_case</c>.</param>
/// <param name="EntityId">Optional filter: one entity's history. It requires <paramref name="EntityKind"/> — sent alone it
/// is a 422 on <c>/entity_kind</c>, because the entity key index starts with the kind and the id would otherwise be a
/// residual filter over the whole project timeline.</param>
/// <param name="Source">Optional filter: <c>manual</c>, <c>ai</c> or <c>import</c>.</param>
/// <param name="ActorMembershipId">Optional filter: the authoring membership that wrote the change.</param>
/// <param name="From">Optional inclusive lower bound, an RFC 3339 instant.</param>
/// <param name="To">Optional <b>exclusive</b> upper bound, an RFC 3339 instant.</param>
/// <param name="Cursor">The opaque <c>(created_at, id)</c> cursor, or null for the first page.</param>
/// <param name="Limit">The page size the route already bounded (1..200).</param>
internal sealed record ListAuthoringVersionsQuery(
    Guid ProjectId,
    string? EntityKind,
    Guid? EntityId,
    string? Source,
    Guid? ActorMembershipId,
    string? From,
    string? To,
    string? Cursor,
    int Limit) : IQuery<Result<AuthoringVersionListResponse>>;

/// <summary>
/// Handler of <see cref="ListAuthoringVersionsQuery"/>: the project gate, then <b>two</b> statements in a
/// <b>read-only</b> transaction (the project-liveness probe and one keyset page) whatever the page size, and a 422 per
/// unsupported filter value — including the combination the statements cannot serve, <c>entity_id</c> without
/// <c>entity_kind</c>.
/// </summary>
/// <remarks>
/// The filters are answered here, inside the handler, and the page size and the two uuids at the route edge — the split
/// D-S55-Q3 records for the authoring surface (the delegate answers every <em>shape</em> refusal, the handler every
/// <em>value</em> refusal). The statement is one of three complete constants chosen by which filters are present
/// (<see cref="AuthoringVersionReadSql"/>), never text assembled from the request.
/// </remarks>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class ListAuthoringVersionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<ListAuthoringVersionsQuery, Result<AuthoringVersionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringVersionListResponse>> Handle(
        ListAuthoringVersionsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListAuthoringVersionsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringVersionListResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        var entityKind = AuthoringEndpointText.Blank(query.EntityKind);

        if (entityKind is not null && !AuthoringVersionFilters.EntityKinds.Contains(entityKind, StringComparer.Ordinal))
        {
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "entity_kind is feature_node, checklist_item or manual_test_case (13.1.1 §5 route 22)",
                [new ErrorDetail("/entity_kind", "value_invalid")]));
        }

        // `entity_id` alone is refused: without `entity_kind` the entity key index of AC9
        // (`org_id, project_id, entity_kind, entity_id, version_no`) cannot serve the filter — `entity_id` would be a
        // residual predicate — so an unknown or another project's id would scan the whole project timeline. The pair is
        // also the only shape the statements serve (<see cref="AuthoringVersionStatement.Pick"/>).
        if (query.EntityId is not null && entityKind is null)
        {
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "entity_id requires entity_kind (13.1.1 §5 route 22)",
                [new ErrorDetail("/entity_kind", "required_with_entity_id")]));
        }

        var source = AuthoringEndpointText.Blank(query.Source);

        if (source is not null && !AuthoringVersionFilters.Sources.Contains(source, StringComparer.Ordinal))
        {
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "source is manual, ai or import (13.1.1 §5 route 22)",
                [new ErrorDetail("/source", "value_invalid")]));
        }

        if (AuthoringVersionInstant.Read(AuthoringEndpointText.Blank(query.From), "/from", out var from) is { } fromError)
        {
            return Result.Failure<AuthoringVersionListResponse>(fromError);
        }

        if (AuthoringVersionInstant.Read(AuthoringEndpointText.Blank(query.To), "/to", out var to) is { } toError)
        {
            return Result.Failure<AuthoringVersionListResponse>(toError);
        }

        if (from is { } lower && to is { } upper && lower > upper)
        {
            // Half-open on purpose (from inclusive, to exclusive), so an empty window is a caller error rather than a
            // silently empty page a client would read as "no history".
            return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                "from must not be later than to (13.1.1 §5 route 22)",
                [new ErrorDetail("/from", "invalid_range")]));
        }

        var cursorCreatedAt = AuthoringVersionCursor.FirstPageTimestamp;
        var cursorId = AuthoringVersionCursor.FirstPageId;

        if (AuthoringEndpointText.Blank(query.Cursor) is { } rawCursor)
        {
            if (!AuthoringVersionCursor.TryDecode(rawCursor, out cursorCreatedAt, out cursorId))
            {
                return Result.Failure<AuthoringVersionListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("/cursor", "malformed")]));
            }
        }

        if (await db.ScalarAsync<int?>(AuthoringVersionReadSql.ProjectExists, new { org_id = orgId, project_id = query.ProjectId }, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Failure<AuthoringVersionListResponse>(AuthoringRules.ProjectNotFound);
        }

        var sql = AuthoringVersionStatement.Pick(entityKind, query.EntityId);

        var rows = await db.QueryAsync<AuthoringVersionRow>(
            sql,
            new AuthoringVersionPageParameters(
                org_id: orgId,
                project_id: query.ProjectId,
                cursor_created_at: cursorCreatedAt,
                cursor_id: cursorId,
                entity_kind: entityKind,
                entity_id: query.EntityId,
                source: source,
                actor: query.ActorMembershipId,
                from_at: from,
                to_at: to,
                take: query.Limit + 1),
            cancellationToken).ConfigureAwait(false);

        // `limit + 1` rows tell a last page from a full one: the extra row is read and dropped, never returned.
        var hasMore = rows.Count > query.Limit;
        var size = hasMore ? query.Limit : rows.Count;
        var items = new List<AuthoringVersionListItemDto>(size);

        for (var index = 0; index < size; index++)
        {
            items.Add(rows[index].ToListItem());
        }

        return Result.Success(new AuthoringVersionListResponse(
            items,
            hasMore ? AuthoringVersionCursor.Encode(rows[size - 1].CreatedAt, rows[size - 1].VersionId) : null));
    }
}

/// <summary>One stored version with its <c>content</c> (13.1.1 §5 route 23, AC9).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="VersionId">The version id the route names.</param>
internal sealed record GetAuthoringVersionQuery(Guid ProjectId, Guid VersionId) : IQuery<Result<AuthoringVersionDetailDto>>;

/// <summary>
/// Handler of <see cref="GetAuthoringVersionQuery"/>: one statement, no separate project probe — the statement carries
/// the project and the project's liveness, so a version of another project, another tenant or a random id is one 404
/// the tenant policy decides (AC10), and the content of a row that is not this version can never be answered.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetAuthoringVersionQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetAuthoringVersionQuery, Result<AuthoringVersionDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringVersionDetailDto>> Handle(
        GetAuthoringVersionQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetAuthoringVersionQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringVersionDetailDto>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        var rows = await db.QueryAsync<AuthoringVersionRow>(
            AuthoringVersionReadSql.VersionDetail,
            new { org_id = orgId, project_id = query.ProjectId, version_id = query.VersionId },
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0
            ? Result.Failure<AuthoringVersionDetailDto>(AuthoringErrors.NotFound(
                "no authoring version with that id is visible in this project"))
            : Result.Success(rows[0].ToDetail());
    }
}

/// <summary>One row of a version page (13.1.1 §5 route 22).</summary>
/// <param name="VersionId">The <c>authoring_version.id</c>.</param>
/// <param name="EntityKind"><c>feature_node</c>, <c>checklist_item</c> or <c>manual_test_case</c>.</param>
/// <param name="EntityId">The entity the version describes.</param>
/// <param name="VersionNo">The entity's version number at the time of the change.</param>
/// <param name="BatchId">The change the row belongs to: the UI groups one batch into one version.</param>
/// <param name="ChangeKind"><c>create</c>, <c>update</c>, <c>delete</c>, <c>restore</c> or <c>import</c>.</param>
/// <param name="Source"><c>manual</c>, <c>ai</c> or <c>import</c>.</param>
/// <param name="ContentHash">The <c>sha256:&lt;hex&gt;</c> digest of the stored content.</param>
/// <param name="Author">The authoring membership that wrote the change, or null.</param>
/// <param name="Reason">The caller's reason, or null.</param>
/// <param name="AiRunId">The generation run of an AI change, or null.</param>
/// <param name="CreatedAt">When the row was appended.</param>
internal sealed record AuthoringVersionListItemDto(
    Guid VersionId,
    string EntityKind,
    Guid EntityId,
    int VersionNo,
    Guid BatchId,
    string ChangeKind,
    string Source,
    string ContentHash,
    Guid? Author,
    string? Reason,
    Guid? AiRunId,
    DateTimeOffset CreatedAt);

/// <summary>One keyset page of versions (route 22); <paramref name="NextCursor"/> is null on the last page.</summary>
/// <param name="Items">The page, in the fixed <c>(created_at desc, id desc)</c> order.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null when this page is the last.</param>
internal sealed record AuthoringVersionListResponse(IReadOnlyList<AuthoringVersionListItemDto> Items, string? NextCursor);

/// <summary>
/// One stored version (route 23): the row members of route 22 and the stored <c>content</c>, passed through as the JSON
/// the writer hashed. The response's <c>ETag</c> is <see cref="ContentHash"/> quoted, and the content is never
/// re-serialised through a typed model.
/// </summary>
/// <param name="VersionId">The <c>authoring_version.id</c>.</param>
/// <param name="EntityKind">The entity kind.</param>
/// <param name="EntityId">The entity the version describes.</param>
/// <param name="VersionNo">The entity's version number at the time of the change.</param>
/// <param name="BatchId">The change the row belongs to.</param>
/// <param name="ChangeKind">The change kind.</param>
/// <param name="Source">Where the change came from.</param>
/// <param name="ContentHash">The <c>sha256:&lt;hex&gt;</c> digest of <paramref name="Content"/>.</param>
/// <param name="Author">The authoring membership that wrote the change, or null.</param>
/// <param name="Reason">The caller's reason, or null.</param>
/// <param name="AiRunId">The generation run of an AI change, or null.</param>
/// <param name="CreatedAt">When the row was appended.</param>
/// <param name="Content">The stored document, verbatim.</param>
internal sealed record AuthoringVersionDetailDto(
    Guid VersionId,
    string EntityKind,
    Guid EntityId,
    int VersionNo,
    Guid BatchId,
    string ChangeKind,
    string Source,
    string ContentHash,
    Guid? Author,
    string? Reason,
    Guid? AiRunId,
    DateTimeOffset CreatedAt,
    JsonElement Content);

/// <summary>
/// The filter vocabularies of route 22 and the shape selector of its page statement, in one place so the served
/// statement and its predicates cannot drift.
/// </summary>
internal static class AuthoringVersionFilters
{
    /// <summary>The three <c>entity_kind</c> values of AC9, spelled as the column stores them.</summary>
    internal static readonly string[] EntityKinds =
        [.. Enum.GetValues<AuthoringEntityKind>().Select(AuthoringWireValues.EntityKindName)];

    /// <summary>The three <c>source</c> values of AC9, spelled as the column stores them.</summary>
    internal static readonly string[] Sources =
        [.. Enum.GetValues<AuthoringSource>().Select(AuthoringWireValues.SourceName)];
}

/// <summary>
/// Picks one of the three complete timeline statements of <see cref="AuthoringVersionReadSql"/> for a request. The
/// alternation happens in C#, never in SQL text (00 §5.7 S1, the OI-13-112 rule): a caller-supplied value is a Dapper
/// parameter only.
/// </summary>
/// <remarks>
/// The handler refuses <c>entity_id</c> without <c>entity_kind</c>, so <paramref name="entityId"/> is never present on
/// its own and the timeline statement never sees an entity id. The kind shape keeps the shared
/// <c>@entity_id is null or …</c> form of the one-parameter-set pattern even though the pair now always goes to
/// <see cref="AuthoringVersionReadSql.EntityPage"/>; the entity shape carries both halves as index conditions.
/// </remarks>
internal static class AuthoringVersionStatement
{
    /// <summary>The statement that serves this filter combination.</summary>
    /// <param name="entityKind">The validated <c>entity_kind</c> filter, or null.</param>
    /// <param name="entityId">The <c>entity_id</c> filter, or null; never present without
    /// <paramref name="entityKind"/>.</param>
    internal static string Pick(string? entityKind, Guid? entityId) =>
        entityKind is not null
            ? entityId is not null ? AuthoringVersionReadSql.EntityPage : AuthoringVersionReadSql.EntityKindPage
            : AuthoringVersionReadSql.TimelinePage;
}

/// <summary>
/// The parameters of one timeline statement: one shared record for the three statements of
/// <see cref="AuthoringVersionReadSql"/>, so a page never binds a name a statement does not know. A member a statement's
/// text does not reference is simply not sent — the timeline shape leaves <c>@entity_id</c> unused, because the handler
/// refuses that id without <c>entity_kind</c> — and Npgsql ignores a declared named parameter the command never uses.
/// The first page passes the sentinel cursor pair, so the seek is always the same row comparison.
/// </summary>
/// <param name="org_id">The open tenant scope's organization.</param>
/// <param name="project_id">The project from the route.</param>
/// <param name="cursor_created_at">The last row's <c>created_at</c>, or the first-page sentinel.</param>
/// <param name="cursor_id">The last row's id, or the first-page sentinel.</param>
/// <param name="entity_kind">The validated <c>entity_kind</c> filter, or null.</param>
/// <param name="entity_id">The <c>entity_id</c> filter, or null; never present without
/// <paramref name="entity_kind"/> (the handler refuses the pair).</param>
/// <param name="source">The validated <c>source</c> filter, or null.</param>
/// <param name="actor">The <c>actor</c> (author membership) filter, or null.</param>
/// <param name="from_at">The inclusive lower bound in UTC, or null.</param>
/// <param name="to_at">The exclusive upper bound in UTC, or null.</param>
/// <param name="take">The page size plus one, so the handler can tell a last page from a full one.</param>
/// <remarks>
/// The three time members are <see cref="DateTimeOffset"/> on purpose: Npgsql maps that CLR type to
/// <c>timestamptz</c> unambiguously, whereas a <see cref="DateTime"/> of the wrong <see cref="DateTimeKind"/> is a
/// <c>timestamp</c> (or a refusal), which would cast the value through the session's zone. The two bounds arrive with a
/// zero offset because <see cref="AuthoringVersionInstant"/> normalises whatever offset the caller spelled — Npgsql
/// refuses a non-zero one for <c>timestamptz</c>.
/// </remarks>
internal sealed record AuthoringVersionPageParameters(
    Guid org_id,
    Guid project_id,
    DateTimeOffset cursor_created_at,
    Guid cursor_id,
    string? entity_kind,
    Guid? entity_id,
    string? source,
    Guid? actor,
    DateTimeOffset? from_at,
    DateTimeOffset? to_at,
    int take);

/// <summary>
/// The opaque <c>cursor</c> of route 22: the <c>(created_at, id)</c> pair the keyset seeks past, encoded
/// <c>v1&lt;newline&gt;&lt;unix microseconds&gt;&lt;newline&gt;&lt;uuid&gt;</c> and Base64URL-encoded so a client cannot
/// read it as a timestamp and cannot be tempted to build one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Microsecond-exact by construction.</b> <c>authoring_version.created_at</c> is <c>timestamptz</c> (microseconds) and
/// a .NET tick is 100 ns, so the wire unit is the Unix <b>microsecond</b>: the codec divides by ten when it encodes (a
/// Postgres value is always a whole number of microseconds) and multiplies by ten when it decodes, and the parameter is
/// sent as a <see cref="DateTimeOffset"/>, which Npgsql binds as <c>timestamptz</c>. No row is
/// therefore skipped or repeated at a page boundary, even when several rows share one <c>created_at</c> and the pages
/// have size one.
/// </para>
/// <para>
/// <b>Strictly validated.</b> Only the canonical output of <see cref="Encode"/> decodes: the alphabet is Base64URL with
/// no padding, the payload is exactly three members, the microsecond half is canonical decimal digits inside the
/// <see cref="DateTimeOffset"/> range, the id is a non-empty lower-case "D" uuid, and the decode re-encodes the value and
/// compares it ordinally, so an appended byte, a re-padded string, an alternative Base64 spelling or a NUL (which is
/// outside the alphabet) is refused instead of guessed. A structurally valid cursor for another instant is not
/// distinguishable from one this route issued — it needs no secret, because the statement always carries
/// <c>org_id</c> and <c>project_id</c>, so a cursor can only ever address rows of the project the URL names.
/// </para>
/// </remarks>
internal static class AuthoringVersionCursor
{
    /// <summary>The cursor version tag, so a future format can be recognised instead of misread.</summary>
    private const string Prefix = "v1";

    /// <summary>The separator between the three members; a uuid "D" form and decimal digits cannot contain it.</summary>
    private const char Separator = '\n';

    /// <summary>The longest cursor this codec can produce (a long microseconds half and a uuid), with slack.</summary>
    private const int MaximumLength = 128;

    /// <summary>The ticks of the Unix epoch, the origin of the microsecond half.</summary>
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    /// <summary>
    /// The timestamp of the first page's sentinel pair: later than any stored <c>clock_timestamp()</c>, so
    /// <c>(created_at, id) &lt; (sentinel, max uuid)</c> is true for every row while staying one searchable row
    /// comparison. It is a plain value, deliberately one second short of <see cref="DateTime.MaxValue"/> so Npgsql's
    /// <c>timestamptz</c> infinity mapping is never involved.
    /// </summary>
    internal static readonly DateTimeOffset FirstPageTimestamp =
        new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>The id of the first page's sentinel pair: the largest <c>uuid</c> in the column's own ordering.</summary>
    internal static readonly Guid FirstPageId = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>Encodes one row's keyset pair as the next page's cursor.</summary>
    /// <param name="createdAt">That row's <c>created_at</c>.</param>
    /// <param name="id">That row's id, the tiebreaker of the total order.</param>
    internal static string Encode(DateTimeOffset createdAt, Guid id) =>
        Encode(Microseconds(createdAt), id);

    /// <summary>
    /// Decodes a cursor, or answers false for anything this codec did not produce — see the type remarks for the list of
    /// refusals. A false answer is the route's 422 <c>/cursor</c> <c>malformed</c>, never a guessed page position.
    /// </summary>
    /// <param name="cursor">The raw query value.</param>
    /// <param name="createdAt">The decoded instant on success.</param>
    /// <param name="id">The decoded id on success.</param>
    internal static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = FirstPageTimestamp;
        id = FirstPageId;

        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumLength)
        {
            return false;
        }

        foreach (var character in cursor)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                // Base64URL only: this is also what refuses a NUL, a '+', a '/' and the '=' padding.
                return false;
            }
        }

        var text = cursor.Replace('-', '+').Replace('_', '/');
        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var members = decoded.Split(Separator);

        if (members.Length != 3
            || !string.Equals(members[0], Prefix, StringComparison.Ordinal)
            || !TryReadMicroseconds(members[1], out var microseconds)
            || !TryReadId(members[2], out id))
        {
            return false;
        }

        createdAt = new DateTimeOffset(UnixEpochTicks + (microseconds * 10), TimeSpan.Zero);

        // The canonical form is the only accepted one: re-encoding cannot change a legitimate cursor, and it refuses a
        // padded, re-spelled or appended variant that parsed by accident.
        return string.Equals(Encode(microseconds, id), cursor, StringComparison.Ordinal);
    }

    /// <summary>Encodes the two members.</summary>
    /// <param name="microseconds">Non-negative Unix microseconds, inside the <see cref="DateTimeOffset"/> range.</param>
    /// <param name="id">The row's id.</param>
    private static string Encode(long microseconds, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Separator}{microseconds}{Separator}{id:D}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The Unix microseconds of an instant, truncated to the database's precision (which is exact for a stored
    /// value).</summary>
    /// <param name="createdAt">The instant.</param>
    private static long Microseconds(DateTimeOffset createdAt) => (createdAt.UtcTicks - UnixEpochTicks) / 10;

    /// <summary>Reads the microseconds half: canonical decimal digits, non-negative, and inside the tick range.</summary>
    /// <param name="text">The member's text.</param>
    /// <param name="microseconds">The decoded value on success.</param>
    private static bool TryReadMicroseconds(string text, out long microseconds)
    {
        microseconds = 0;

        if (text.Length is 0 or > 19 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out microseconds) || microseconds < 0)
        {
            return false;
        }

        // The value must be representable: the tick range ends far below long.MaxValue, so a cursor from another unit
        // (nanoseconds, milliseconds) is refused here instead of overflowing the DateTime arithmetic.
        return microseconds <= (DateTime.MaxValue.Ticks - UnixEpochTicks) / 10;
    }

    /// <summary>Reads the id half: a non-empty uuid in its canonical lower-case "D" spelling.</summary>
    /// <param name="text">The member's text.</param>
    /// <param name="id">The decoded id on success.</param>
    private static bool TryReadId(string text, out Guid id)
    {
        id = Guid.Empty;

        if (!Guid.TryParse(text, out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), text, StringComparison.Ordinal))
        {
            return false;
        }

        id = parsed;

        return true;
    }
}

/// <summary>
/// The RFC 3339 instant reader of route 22's <c>from</c>/<c>to</c>: an absent value reads as null, anything that is not a
/// date-time with an explicit offset is a 422 naming the query parameter (never an echoed value), and an accepted value
/// is normalised to UTC.
/// </summary>
/// <remarks>
/// <b>Always UTC, never the caller's offset.</b> <c>authoring_version.created_at</c> is <c>timestamptz</c>, which stores
/// an instant and no zone, and Npgsql refuses to write a <see cref="DateTimeOffset"/> whose offset is not zero to that
/// type: a bound of <c>2026-10-01T10:00:00+07:00</c> would be a write error instead of a page. The reader therefore keeps
/// the caller's instant and drops its spelling (<see cref="DateTimeOffset.ToUniversalTime"/>), so <c>+07:00</c>,
/// <c>-05:30</c> and <c>Z</c> are the same window.
/// </remarks>
internal static class AuthoringVersionInstant
{
    /// <summary>The longest instant this reader accepts (RFC 3339 allows 10 fractional digits; 40 covers them).</summary>
    private const int MaximumLength = 40;

    /// <summary>Reads one bound; absent → null with no error.</summary>
    /// <param name="raw">The raw query value, or null.</param>
    /// <param name="path">The query parameter's pointer, e.g. <c>/from</c>.</param>
    /// <param name="instant">The instant on success.</param>
    internal static Error? Read(string? raw, string path, out DateTimeOffset? instant)
    {
        instant = null;

        if (raw is null)
        {
            return null;
        }

        if (!TryParse(raw, out var parsed))
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"{path[1..]} must be an RFC 3339 instant with an explicit offset, e.g. 2026-10-01T10:00:00Z"),
                [new ErrorDetail(path, "value_invalid")]);
        }

        // UTC, not the caller's spelling: Npgsql binds a DateTimeOffset with a non-zero offset to `timestamptz` as a
        // write error, so the offset is dropped here and only the instant survives.
        instant = parsed.ToUniversalTime();

        return null;
    }

    /// <summary>
    /// Parses an RFC 3339 date-time: a <c>T</c> separator, a time with hours, minutes and seconds, and a <c>Z</c> or a
    /// <c>±hh:mm</c> offset. A bare date, a local time without an offset and any other spelling are refused, because an
    /// instant without an offset would silently be read in the server's zone.
    /// </summary>
    /// <param name="raw">The raw value.</param>
    /// <param name="instant">The parsed instant on success.</param>
    private static bool TryParse(string raw, out DateTimeOffset instant)
    {
        instant = default;

        if (raw.Length is 0 or > MaximumLength || AuthoringText.ContainsNul(raw))
        {
            return false;
        }

        var separator = raw.IndexOf('T', StringComparison.Ordinal);

        if (separator < 0)
        {
            separator = raw.IndexOf('t', StringComparison.Ordinal);
        }

        if (separator <= 0)
        {
            return false;
        }

        var time = raw[separator..];
        var seconds = time.Count(character => character == ':');

        if (seconds < 2 || !HasOffset(raw))
        {
            return false;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant);
    }

    /// <summary>True when the value ends with <c>Z</c>/<c>z</c> or a <c>±hh:mm</c> offset.</summary>
    /// <param name="raw">The raw value.</param>
    private static bool HasOffset(string raw)
    {
        if (raw[^1] is 'Z' or 'z')
        {
            return true;
        }

        return raw.Length > 6
            && raw[^3] == ':'
            && raw[^6] is '+' or '-'
            && char.IsAsciiDigit(raw[^5])
            && char.IsAsciiDigit(raw[^4])
            && char.IsAsciiDigit(raw[^2])
            && char.IsAsciiDigit(raw[^1]);
    }
}

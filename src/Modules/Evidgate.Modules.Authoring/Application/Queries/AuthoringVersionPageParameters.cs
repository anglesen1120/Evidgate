using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Queries;

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

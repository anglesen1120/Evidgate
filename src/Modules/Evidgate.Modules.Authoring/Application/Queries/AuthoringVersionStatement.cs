using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Queries;

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

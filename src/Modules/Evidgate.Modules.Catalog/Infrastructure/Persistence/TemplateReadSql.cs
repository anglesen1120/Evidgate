using System.Text.Json;
using Evidgate.Modules.Catalog.Contracts;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>The Dapper statements of the template registry, one statement per handler. RLS scopes every row to the tenant.</summary>
internal static class TemplateReadSql
{
    private const string TemplateColumns = """
               t.id, t.kind, t.name, t.version, t.body_ref, t.created_at
        """;

    /// <summary>First page of the active templates; a null kind is every kind.</summary>
    internal const string TemplatesFirstPage = $"""
        select {TemplateColumns}
          from public.template t
         where t.active
           and (@kind::text is null or t.kind = @kind)
         order by t.created_at,
                  t.id
         limit @limit
        """;

    internal const string TemplatesNextPage = $"""
        select {TemplateColumns}
          from public.template t
         where t.active
           and (@kind::text is null or t.kind = @kind)
           and (t.created_at, t.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by t.created_at,
                  t.id
         limit @limit
        """;

    /// <summary>
    /// One active template by id. An inactive row, another tenant's row (RLS hides it) and a missing id match no
    /// row, so all three answer the same 404.
    /// </summary>
    internal const string TemplateById = $"""
        select {TemplateColumns},
               t.conformance_rules::text as conformance_rules
          from public.template t
         where t.id = @template_id
           and t.active
        """;
}

/// <summary>One summary row of <see cref="TemplateReadSql"/>.</summary>
internal sealed record TemplateRow(
    Guid Id,
    string Kind,
    string Name,
    int Version,
    string BodyRef,
    DateTimeOffset CreatedAt)
{
    /// <summary>The wire shape of the row.</summary>
    internal TemplateDto ToDto() => new(Id, Kind, Name, Version, BodyRef, CreatedAt);
}

/// <summary>One detail row: the summary plus the stored rules document as text.</summary>
internal sealed record TemplateDetailRow(
    Guid Id,
    string Kind,
    string Name,
    int Version,
    string BodyRef,
    string ConformanceRules,
    DateTimeOffset CreatedAt)
{
    /// <summary>The wire shape of the row, with the rules decoded once into an element.</summary>
    internal TemplateDetailDto ToDto() => new(Id, Kind, Name, Version, BodyRef, ReadRules(ConformanceRules), CreatedAt);

    /// <summary>A stored rules document. The column's CHECK bounds it, so a row this module serves always parses.</summary>
    private static JsonElement ReadRules(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

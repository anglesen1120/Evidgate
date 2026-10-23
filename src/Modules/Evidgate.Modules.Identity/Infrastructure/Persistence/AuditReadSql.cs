using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>Read SQL of the audit API (02.7.1-T3.1): one statement per page, keyset on <c>(created_at, id)</c> descending.</summary>
internal static class AuditReadSql
{
    /// <summary>
    /// One page. Optional filters are <c>null</c>-tolerant; the cursor predicate is the tuple
    /// comparison the <c>(org_id, created_at desc, id desc)</c> index serves. <c>@limit</c> is page size + 1.
    /// </summary>
    internal const string Page =
        """
        select id as Id, seq as Seq, actor_type as ActorType, actor_id as ActorId, action as Action,
               object_type as ObjectType, object_id as ObjectId,
               encode(before_hash, 'hex') as BeforeHash, encode(after_hash, 'hex') as AfterHash,
               request_id as RequestId, trace_id as TraceId, metadata::text as MetadataJson,
               encode(prev_hash, 'hex') as PrevHash, encode(entry_hash, 'hex') as EntryHash, created_at as CreatedAt
          from public.audit_log
         where org_id = @org_id
           and (@actor_type::text is null or actor_type = @actor_type)
           and (@action::text is null or action = @action)
           and (@object_type::text is null or object_type = @object_type)
           and (@from::timestamptz is null or created_at >= @from)
           and (@to::timestamptz is null or created_at < @to)
           and (@cursor_created_at::timestamptz is null or (created_at, id) < (@cursor_created_at, @cursor_id::uuid))
         order by created_at desc, id desc
         limit @limit
        """;
}

/// <summary>Dapper shape of <see cref="AuditReadSql.Page"/>.</summary>
internal sealed class AuditPageRow
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public string ActorType { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? ObjectType { get; set; }

    public string? ObjectId { get; set; }

    public string? BeforeHash { get; set; }

    public string? AfterHash { get; set; }

    public string? RequestId { get; set; }

    public string? TraceId { get; set; }

    public string? MetadataJson { get; set; }

    public string PrevHash { get; set; } = string.Empty;

    public string EntryHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    internal AuditEntryDto ToDto() => new(
        Id,
        Seq,
        ActorType,
        ActorId,
        Action,
        ObjectType,
        ObjectId,
        BeforeHash,
        AfterHash,
        RequestId,
        TraceId,
        MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson),
        PrevHash,
        EntryHash,
        CreatedAt);
}

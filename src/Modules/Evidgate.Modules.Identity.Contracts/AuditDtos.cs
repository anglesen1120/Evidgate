namespace Evidgate.Modules.Identity.Contracts;

/// <summary>One audit event of <c>GET /v1/orgs/{o}/audit</c> (02.7.1 §5). Hashes are lowercase hex.</summary>
/// <param name="Id">Row id.</param>
/// <param name="Seq">Position in the org's chain (dense from 1).</param>
/// <param name="ActorType">user | ci_producer | agent | platform_operator | system.</param>
/// <param name="ActorId">The actor's id.</param>
/// <param name="Action">Action string, e.g. <c>membership.invite</c>.</param>
/// <param name="ObjectType">Kind of the affected object, if recorded.</param>
/// <param name="ObjectId">Id of the affected object, if recorded.</param>
/// <param name="BeforeHash">sha256 of the object before the change, if recorded.</param>
/// <param name="AfterHash">sha256 of the object after the change, if recorded.</param>
/// <param name="RequestId">The 00 §9.2 request id.</param>
/// <param name="TraceId">The W3C trace id.</param>
/// <param name="Metadata">Allowlisted, secret-free context.</param>
/// <param name="PrevHash">The previous row's entry hash (zeros for the first row).</param>
/// <param name="EntryHash">sha256(prev_hash ‖ canonical fields): lets a client re-verify the chain.</param>
/// <param name="CreatedAt">When the row was appended (UTC, microseconds).</param>
public sealed record AuditEntryDto(
    Guid Id,
    long Seq,
    string ActorType,
    string ActorId,
    string Action,
    string? ObjectType,
    string? ObjectId,
    string? BeforeHash,
    string? AfterHash,
    string? RequestId,
    string? TraceId,
    IReadOnlyDictionary<string, string>? Metadata,
    string PrevHash,
    string EntryHash,
    DateTimeOffset CreatedAt);

/// <summary>Cursor page of audit events, newest first (00 §9.1).</summary>
/// <param name="Items">The page ordered by <c>(created_at, id)</c> descending.</param>
/// <param name="NextCursor">Opaque cursor of the next page, or null on the last page.</param>
public sealed record AuditListResponse(IReadOnlyList<AuditEntryDto> Items, string? NextCursor);

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>The <c>actor_type</c> values of <c>audit_log</c> (02.7.1 §5).</summary>
public static class AuditActorTypes
{
    /// <summary>A signed-in member.</summary>
    public const string User = "user";

    /// <summary>A CI service identity (<c>ci_producer</c>).</summary>
    public const string CiProducer = "ci_producer";

    /// <summary>A runner agent (V2).</summary>
    public const string Agent = "agent";

    /// <summary>Evidgate staff acting under a support grant.</summary>
    public const string PlatformOperator = "platform_operator";

    /// <summary>The system itself: scheduled tasks and resolvers.</summary>
    public const string System = "system";
}

/// <summary>Who performed an audited action.</summary>
/// <param name="Type">One of <see cref="AuditActorTypes"/>.</param>
/// <param name="Id">The actor's id: a user id, a token id, or <c>system</c>.</param>
public sealed record AuditActor(string Type, string Id);

/// <summary>
/// What a handler says about the object an audited command changed. The action itself comes
/// from the handler's <c>[AuditedAction]</c>; this is the optional detail.
/// </summary>
/// <param name="ObjectType">Kind of object (<c>membership</c>, <c>project</c>…).</param>
/// <param name="ObjectId">The object's id.</param>
/// <param name="BeforeHash">sha256 of the object's canonical content before the change, if any.</param>
/// <param name="AfterHash">sha256 of its canonical content after the change, if any.</param>
/// <param name="Metadata">Allowlisted, secret-free context (see <see cref="AuditMetadata"/>).</param>
public sealed record AuditObject(
    string ObjectType,
    string ObjectId,
    byte[]? BeforeHash = null,
    byte[]? AfterHash = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>One entry waiting to be appended to the org's chain.</summary>
/// <param name="Action">Action string, e.g. <c>membership.invite</c>.</param>
/// <param name="Actor">Who did it.</param>
/// <param name="Subject">The affected object, if the handler described one.</param>
/// <param name="RequestId">The request id (00 §9.2), if known.</param>
/// <param name="TraceId">The W3C trace id, if a span was active.</param>
public sealed record AuditEntry(
    string Action,
    AuditActor Actor,
    AuditObject? Subject,
    string? RequestId,
    string? TraceId);

/// <summary>A stored <c>audit_log</c> row, as the verifier and the read API see it.</summary>
public sealed record AuditRow(
    Guid Id,
    Guid OrgId,
    long Seq,
    string ActorType,
    string ActorId,
    string Action,
    string? ObjectType,
    string? ObjectId,
    byte[]? BeforeHash,
    byte[]? AfterHash,
    string? RequestId,
    string? TraceId,
    IReadOnlyDictionary<string, string>? Metadata,
    byte[] PrevHash,
    byte[] EntryHash,
    DateTimeOffset CreatedAt);

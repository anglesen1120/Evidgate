using System.Text.Json;

namespace Evidgate.Modules.Policy.Contracts;

/// <summary>
/// Wire shape of one policy revision (07.1.1 §5): the body of <c>POST /v1/projects/{p}/policies</c> and
/// <c>GET /v1/policies/{id}</c>. <c>Body</c> is the stored <c>jsonb</c> as a JSON value, so the caller reads
/// exactly the 00 §8.6 field set that was written.
/// </summary>
/// <param name="Id">The revision; also the <c>ETag</c>.</param>
/// <param name="ProjectId">The project the revision belongs to.</param>
/// <param name="RevisionNo">Its number inside the project (append-only, 1-based).</param>
/// <param name="Hash">The content address, <c>sha256:&lt;64 lower-case hex&gt;</c> (00 §7).</param>
/// <param name="State">The lifecycle state: <c>draft</c>, <c>active</c> or <c>retired</c> (07.1.1 §6).</param>
/// <param name="Body">The stored policy body.</param>
/// <param name="CreatedBy">The member that wrote the revision.</param>
/// <param name="CreatedAt">When it was stored, UTC.</param>
/// <param name="Version">The optimistic-concurrency token (00 §7).</param>
public sealed record PolicyRevisionDto(
    Guid Id,
    Guid ProjectId,
    int RevisionNo,
    string Hash,
    string State,
    JsonElement Body,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>One row of <c>GET /v1/policies/{id}/revisions</c> (07.1.1 §5): a revision without its body.</summary>
/// <param name="Id">The revision.</param>
/// <param name="RevisionNo">Its number inside the project.</param>
/// <param name="Hash">The content address.</param>
/// <param name="State">The lifecycle state.</param>
/// <param name="CreatedBy">The member that wrote the revision.</param>
/// <param name="CreatedAt">When it was stored, UTC.</param>
/// <param name="Version">The optimistic-concurrency token.</param>
public sealed record PolicyRevisionSummaryDto(
    Guid Id,
    int RevisionNo,
    string Hash,
    string State,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>
/// One cursor page of a project's policy revisions (00 §9.1 <c>{items, next_cursor}</c>), ordered
/// <c>revision_no</c> ascending — the append-only order.
/// </summary>
/// <param name="Items">The page's rows.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null on the last page.</param>
public sealed record PolicyRevisionListResponse(
    IReadOnlyList<PolicyRevisionSummaryDto> Items,
    string? NextCursor);

/// <summary>
/// Wire shape of <c>POST /v1/projects/{p}/policy-activation</c> (07.1.1 §5, T4.1): the project's policy
/// pointer after the call. A run created afterwards pins <c>PolicyRevisionId</c>.
/// </summary>
/// <param name="ProjectId">The project whose pointer moved.</param>
/// <param name="PolicyRevisionId">The revision that is now the project's active policy.</param>
/// <param name="Hash">Its content address, <c>sha256:&lt;64 lower-case hex&gt;</c> (00 §7).</param>
/// <param name="Mode">The stored mode: <c>advisory</c> (the only value this story accepts).</param>
/// <param name="ActivatedBy">The member that activated it.</param>
/// <param name="ActivatedAt">When the pointer was written, UTC.</param>
/// <param name="PreviousPolicyRevisionId">
/// The revision this call retired, or null when nothing changed (the target was already active, or the
/// project had no active revision).
/// </param>
/// <param name="Changed">True when this call moved the pointer; false when the target was already active.</param>
public sealed record PolicyActivationDto(
    Guid ProjectId,
    Guid PolicyRevisionId,
    string Hash,
    string Mode,
    Guid ActivatedBy,
    DateTimeOffset ActivatedAt,
    Guid? PreviousPolicyRevisionId,
    bool Changed);

/// <summary>
/// Wire shape of <c>GET /v1/projects/{p}/policy-activation</c> (F18): the project's stored activation pointer.
/// Read-only, so it carries neither <c>previous_policy_revision_id</c> nor <c>changed</c> — both describe one
/// <c>POST</c> call and nothing stores them. A project that was never activated is the same <c>404</c> as an
/// unknown, soft-deleted or other-tenant project.
/// </summary>
/// <param name="ProjectId">The project whose pointer this is.</param>
/// <param name="PolicyRevisionId">The revision that is the project's active policy.</param>
/// <param name="Hash">Its content address, <c>sha256:&lt;64 lower-case hex&gt;</c> (00 §7).</param>
/// <param name="Mode">The stored mode: <c>advisory</c> or <c>enforcing</c>.</param>
/// <param name="ActivatedBy">The member that last moved the pointer.</param>
/// <param name="ActivatedAt">When the pointer was last written, UTC.</param>
public sealed record PolicyActivationStateDto(
    Guid ProjectId,
    Guid PolicyRevisionId,
    string Hash,
    string Mode,
    Guid ActivatedBy,
    DateTimeOffset ActivatedAt);

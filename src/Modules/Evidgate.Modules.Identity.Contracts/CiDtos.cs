namespace Evidgate.Modules.Identity.Contracts;

/// <summary>An ingest token as listed (02.4.2 §5): metadata only, never the secret or its hash.</summary>
/// <param name="Id">Token id.</param>
/// <param name="ProjectId">The project it is scoped to.</param>
/// <param name="Name">Admin-chosen label.</param>
/// <param name="Prefix">The public lookup prefix.</param>
/// <param name="Status"><c>active</c>, <c>rotated</c>, <c>expired</c> or <c>revoked</c>.</param>
/// <param name="Scopes">Permissions the minted token may carry.</param>
/// <param name="ExpiresAt">Hard expiry, if any.</param>
/// <param name="OverlapEndsAt">When a rotated token stops verifying.</param>
/// <param name="LastUsedAt">Last successful exchange (60 s granularity).</param>
/// <param name="RotatedToId">The successor, if rotated.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record IngestTokenView(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Prefix,
    string Status,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? OverlapEndsAt,
    DateTimeOffset? LastUsedAt,
    Guid? RotatedToId,
    DateTimeOffset CreatedAt);

/// <summary>Response of create and rotate: the metadata plus the plaintext token, shown exactly once.</summary>
/// <param name="Token">The metadata of the new token.</param>
/// <param name="Secret">The full <c>egt_…</c> string. It is not stored and cannot be read again.</param>
/// <param name="OverlapEndsAt">For a rotation: when the previous token stops verifying.</param>
public sealed record IngestTokenCreatedResponse(IngestTokenView Token, string Secret, DateTimeOffset? OverlapEndsAt);

/// <summary>Cursor page of ingest tokens.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">Opaque cursor or null.</param>
public sealed record IngestTokenListResponse(IReadOnlyList<IngestTokenView> Items, string? NextCursor);

/// <summary>Body of <c>POST /v1/projects/{p}/ingest-tokens</c>.</summary>
/// <param name="Name">Label shown in the list.</param>
/// <param name="Scopes">Subset of <c>event:write</c>, <c>artifact:write</c>, <c>run:create</c>, <c>decision:read</c>; default all four.</param>
/// <param name="ExpiresAt">Optional hard expiry.</param>
public sealed record CreateIngestTokenRequest(string Name, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt);

/// <summary>Body of the project ingest-token exchange.</summary>
/// <param name="IngestToken">The project ingest token.</param>
public sealed record CiTokenRequest(string? IngestToken);

/// <summary>Response of <c>POST /v1/ci/token</c> (02.4.1 AC1).</summary>
/// <param name="Token">The compact <c>ci_producer</c> JWT.</param>
/// <param name="ExpiresAt">When it stops being valid (at most 60 minutes).</param>
/// <param name="ProjectId">The one project it is scoped to.</param>
/// <param name="PermissionSet">The permissions it carries.</param>
public sealed record CiTokenResponse(string Token, DateTimeOffset ExpiresAt, Guid ProjectId, IReadOnlyList<string> PermissionSet);

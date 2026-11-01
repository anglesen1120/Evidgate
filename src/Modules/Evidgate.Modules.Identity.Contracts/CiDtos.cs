namespace Evidgate.Modules.Identity.Contracts;

/// <summary>A CI binding as listed and read (02.4.1 §5). Never carries the challenge or its hash.</summary>
/// <param name="Id">Binding id.</param>
/// <param name="ProjectId">The project it mints tokens for.</param>
/// <param name="Provider"><c>github_actions</c>.</param>
/// <param name="RepositoryId">The provider's immutable repository id.</param>
/// <param name="RepositoryFullName">Display name; follows renames, never used for trust (D4).</param>
/// <param name="AllowedRefPatterns">Git refs allowed to mint (glob, <c>*</c> only).</param>
/// <param name="AllowedWorkflowRefs">Allowed <c>job_workflow_ref</c> values (glob, <c>*</c> only).</param>
/// <param name="Status"><c>pending_verification</c>, <c>active</c> or <c>disabled</c>.</param>
/// <param name="ActivatedAt">When repository control was proven.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Version">Optimistic-concurrency token (<c>ETag</c>).</param>
public sealed record CiBindingView(
    Guid Id,
    Guid ProjectId,
    string Provider,
    long RepositoryId,
    string RepositoryFullName,
    IReadOnlyList<string> AllowedRefPatterns,
    IReadOnlyList<string> AllowedWorkflowRefs,
    string Status,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>Response of <c>POST /v1/projects/{p}/ci-bindings</c>: the view plus the one-time challenge (T70).</summary>
/// <param name="Binding">The created binding.</param>
/// <param name="BindingChallenge">Shown once; set as the repo variable <c>EVIDGATE_BINDING_CHALLENGE</c>.</param>
public sealed record CiBindingCreatedResponse(CiBindingView Binding, string BindingChallenge);

/// <summary>Cursor page of bindings.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">Opaque cursor or null.</param>
public sealed record CiBindingListResponse(IReadOnlyList<CiBindingView> Items, string? NextCursor);

/// <summary>Body of <c>POST /v1/projects/{p}/ci-bindings</c>.</summary>
/// <param name="Provider">Must be <c>github_actions</c>.</param>
/// <param name="RepositoryId">The immutable repository id.</param>
/// <param name="RepositoryFullName"><c>owner/repo</c>.</param>
/// <param name="AllowedRefPatterns">Refs allowed to mint.</param>
/// <param name="AllowedWorkflowRefs">Workflow files allowed to mint.</param>
public sealed record CreateCiBindingRequest(
    string Provider,
    long RepositoryId,
    string RepositoryFullName,
    IReadOnlyList<string> AllowedRefPatterns,
    IReadOnlyList<string> AllowedWorkflowRefs);

/// <summary>Body of <c>PATCH /v1/ci-bindings/{b}</c>; absent fields are unchanged.</summary>
/// <param name="AllowedRefPatterns">New ref allowlist.</param>
/// <param name="AllowedWorkflowRefs">New workflow allowlist.</param>
/// <param name="Status"><c>disabled</c>, or <c>active</c> to re-enable a binding that was active before.</param>
public sealed record UpdateCiBindingRequest(
    IReadOnlyList<string>? AllowedRefPatterns,
    IReadOnlyList<string>? AllowedWorkflowRefs,
    string? Status);

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

/// <summary>Body of <c>POST /v1/ci/token</c> for the ingest-token grant or a binding challenge.</summary>
/// <param name="IngestToken">The <c>egt_…</c> token (the GitHub grant sends the OIDC JWT as a bearer instead).</param>
/// <param name="BindingChallenge">The one-time challenge, presented by the first exchange of a pending binding.</param>
public sealed record CiTokenRequest(string? IngestToken, string? BindingChallenge);

/// <summary>Response of <c>POST /v1/ci/token</c> (02.4.1 AC1).</summary>
/// <param name="Token">The compact <c>ci_producer</c> JWT.</param>
/// <param name="ExpiresAt">When it stops being valid (at most 60 minutes).</param>
/// <param name="ProjectId">The one project it is scoped to.</param>
/// <param name="PermissionSet">The permissions it carries.</param>
public sealed record CiTokenResponse(string Token, DateTimeOffset ExpiresAt, Guid ProjectId, IReadOnlyList<string> PermissionSet);

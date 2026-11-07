namespace Evidgate.Modules.Identity.Contracts;

/// <summary>Body of <c>POST /v1/projects/{p}/api-tokens</c>.</summary>
/// <param name="Name">Label shown in the list.</param>
/// <param name="Permissions">The permission subset the token carries; never an administrative code.</param>
/// <param name="ExpiresAt">Hard expiry; required and at most the deployment's maximum lifetime.</param>
public sealed record CreateApiTokenRequest(string Name, IReadOnlyList<string>? Permissions, DateTimeOffset? ExpiresAt);

/// <summary>Response of the create route: the metadata plus the plaintext token, shown exactly once.</summary>
/// <param name="Id">Token id.</param>
/// <param name="Name">Label chosen by the creator.</param>
/// <param name="Prefix">The public lookup prefix.</param>
/// <param name="Token">The full <c>evg_…</c> string. It is not stored and cannot be read again.</param>
/// <param name="Permissions">The subset the token will carry.</param>
/// <param name="ExpiresAt">When the token stops authenticating.</param>
public sealed record ApiTokenCreatedResponse(
    Guid Id,
    string Name,
    string Prefix,
    string Token,
    IReadOnlyList<string> Permissions,
    DateTimeOffset ExpiresAt);

/// <summary>An API token as listed: metadata only, never the plaintext or its HMAC.</summary>
/// <param name="Id">Token id.</param>
/// <param name="Name">Label chosen by the creator.</param>
/// <param name="Prefix">The public lookup prefix.</param>
/// <param name="Permissions">The subset the token carries.</param>
/// <param name="Status"><c>active</c>, <c>expired</c> or <c>revoked</c>.</param>
/// <param name="ExpiresAt">When the token stops authenticating.</param>
/// <param name="LastUsedAt">Last successful use, or null while unused.</param>
/// <param name="CreatedBy">The member who created it.</param>
public sealed record ApiTokenView(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Permissions,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    Guid CreatedBy);

/// <summary>Cursor page of API tokens.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">Opaque cursor or null.</param>
public sealed record ApiTokenListResponse(IReadOnlyList<ApiTokenView> Items, string? NextCursor);

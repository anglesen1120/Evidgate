namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The answer of <c>POST /v1/projects/{p}/tracker-connections/{provider}/authorize</c>: where to send the browser. The
/// URL carries the single-use state; it is never stored in clear and never logged.
/// </summary>
/// <param name="AuthorizationUrl">The provider's consent page, always on its fixed host.</param>
/// <param name="ExpiresAt">When the consent expires unused.</param>
public sealed record TrackerAuthorizeResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);

/// <summary>What a connection can do. Read-only until a later consent adds write access.</summary>
/// <param name="CreateIssue">Always false: no connection holds write consent.</param>
public sealed record TrackerCapabilitiesDto(bool CreateIssue);

/// <summary>One tracker connection as the status routes report it. No token, cipher or provider id is ever returned.</summary>
/// <param name="Id">The connection, which disconnect addresses.</param>
/// <param name="Provider">One of <c>jira_cloud</c>, <c>linear</c>.</param>
/// <param name="State">One of <c>active</c>, <c>needs_reauth</c>, <c>disconnected</c>.</param>
/// <param name="Capabilities">What the connection may do.</param>
/// <param name="GrantedScopes">The scopes the provider granted.</param>
/// <param name="CreatedAt">When the connection was made.</param>
public sealed record TrackerConnectionDto(
    Guid Id,
    string Provider,
    string State,
    TrackerCapabilitiesDto Capabilities,
    IReadOnlyList<string> GrantedScopes,
    DateTimeOffset CreatedAt);

/// <summary>The answer of <c>GET /v1/projects/{p}/tracker-connections</c>: every connection of the project, newest last.</summary>
/// <param name="Items">The connections.</param>
public sealed record TrackerConnectionListResponse(IReadOnlyList<TrackerConnectionDto> Items);

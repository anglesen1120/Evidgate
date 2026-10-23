namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Body of <c>POST /v1/orgs/{o}/memberships</c> (02.1.2 §5): invite a colleague by
/// address, optionally with the roles the membership starts with. The created row is
/// <c>pending</c>: it becomes <c>active</c> only when the invite's single-use secret
/// is presented <em>and</em> a login reports the same address as provider-verified
/// (T75, AC2) — email alone never binds.
/// </summary>
/// <param name="Email">
/// The invited address. Normalised (trimmed, lowercased) before it is stored or
/// compared (02.1.2 §5), so <c>A@B</c> and <c>a@b</c> are one invite (AC1).
/// </param>
/// <param name="Roles">
/// Role ids the membership starts with, or null/empty for none. Each id must be a
/// built-in catalogue role or the caller's own org row; anything else is 409
/// <c>unknown_role</c> (02.1.2 §5).
/// </param>
public sealed record InviteMemberRequest(string Email, IReadOnlyList<Guid>? Roles);

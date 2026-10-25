namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// What <see cref="IMembershipBinder.BindAsync"/> changed (02.1.2-T2, T75): the
/// memberships this call activated, one entry per org whose transaction committed.
/// <para>
/// Empty is the ordinary answer, not a failure: a login with no invite token, an
/// unverified or absent email, a token of another org, an expired or already used
/// invite, and an address the invite does not name all produce the same empty list
/// (T75 — the login flow must not become an oracle that tells an attacker which of
/// those happened). Only a quota refusal fails the call, with 429
/// <c>quota_exceeded</c>.
/// </para>
/// </summary>
/// <param name="Bound">The memberships activated by this call, in the order they were processed.</param>
public sealed record BindPendingMembershipsResult(IReadOnlyList<BoundMembership> Bound);

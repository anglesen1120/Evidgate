namespace Evidgate.Modules.Policy.Domain;

/// <summary>States of <c>waiver.state</c> (07.3.1 §6, 00 §8.8).</summary>
internal static class WaiverState
{
    /// <summary>Requested by one member, not yet approved by another.</summary>
    public const string Requested = "requested";

    /// <summary>Approved by a member other than the requester; the risk is accepted.</summary>
    public const string Approved = "approved";

    /// <summary>Past its <c>expires_at</c>; terminal, written only by the 07.3.2-T2 sweeper.</summary>
    public const string Expired = "expired";

    /// <summary>Withdrawn before or after approval; terminal.</summary>
    public const string Revoked = "revoked";
}

/// <summary>The scopes a waiver may name (07.3.1 §4 <c>waiver_scope_check</c>).</summary>
internal static class WaiverScope
{
    /// <summary>The waiver covers one case.</summary>
    public const string Case = "case";

    /// <summary>The waiver covers a whole suite.</summary>
    public const string Suite = "suite";

    /// <summary>True when <paramref name="value"/> is one of the two stored scopes.</summary>
    public static bool IsKnown(string? value) => value is Case or Suite;
}

/// <summary>
/// Shape and transition rules of a waiver (07.3.1 §4, §6, FR-07-8). The API checks them before the write; the
/// database repeats the self-approval rule as <c>waiver_self_approval_check</c> and the transitions in
/// <c>evidgate.waiver_guard()</c>, so a bug here cannot launder a risk acceptance.
/// </summary>
internal static class WaiverRules
{
    /// <summary>The <c>waiver_reason_check</c> bound (07.3.1 §4).</summary>
    public const int ReasonMaxLength = 2000;

    /// <summary>True only from <c>requested</c>: a waiver is never re-approved from a terminal state (07.3.1 §6).</summary>
    public static bool CanApprove(string state) => state == WaiverState.Requested;

    /// <summary>True from <c>requested</c> and <c>approved</c>: a requester or approver may withdraw (07.3.1 §6).</summary>
    public static bool CanRevoke(string state) => state is WaiverState.Requested or WaiverState.Approved;

    /// <summary>True when the approver is the requester — refused 403 <c>waiver_self_approval</c> (AC1, FR-07-8).</summary>
    public static bool IsSelfApproval(Guid requestedBy, Guid approver) => requestedBy == approver;
}

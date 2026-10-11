namespace Evidgate.Modules.Policy.Domain;

/// <summary>States of <c>quarantine_entry.state</c> (07.3.1 §6, 00 §8.8).</summary>
internal static class QuarantineState
{
    /// <summary>Excludes the case from gating while its failure stays visible.</summary>
    public const string Active = "active";

    /// <summary>Past its <c>expires_at</c>; terminal, written only by the 07.3.2-T2 sweeper.</summary>
    public const string Expired = "expired";

    /// <summary>Removed by an explicit <c>DELETE</c>; terminal.</summary>
    public const string Removed = "removed";
}

/// <summary>
/// Shape and transition rules of a quarantine entry (07.3.1 §4, §6, FR-07-9). The API checks them before the write;
/// the database repeats the transitions in <c>evidgate.quarantine_entry_guard()</c>, so a bug here cannot launder a
/// critical case into a pass.
/// </summary>
internal static class QuarantineRules
{
    /// <summary>The <c>quarantine_entry_reason_check</c> bound (07.3.1 §4).</summary>
    public const int ReasonMaxLength = 2000;

    /// <summary>True only from <c>active</c>: a removed or expired entry is never re-removed (07.3.1 §6, AC6).</summary>
    public static bool CanRemove(string state) => state == QuarantineState.Active;
}

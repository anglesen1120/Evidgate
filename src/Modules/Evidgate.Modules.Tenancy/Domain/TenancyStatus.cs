namespace Evidgate.Modules.Tenancy.Domain;

/// <summary>
/// The canonical <c>organization.status</c> / <c>project.status</c> values
/// (00 §8.9, feature plan §6, and the check constraint of migrations 0004/0005).
/// The transitions themselves (<c>active → suspended → active</c>,
/// <c>active|suspended → deleted</c>) are enforced by the 02.1.1-T2 commands, so
/// this type only names the closed value set the column accepts.
/// </summary>
internal static class TenancyStatus
{
    /// <summary>A usable tenant: reads succeed and mutations are allowed.</summary>
    public const string Active = "active";

    /// <summary>Reads still return data, mutations are rejected with 409 <c>org_suspended</c>.</summary>
    public const string Suspended = "suspended";

    /// <summary>Tombstone written by the operator deletion route (06/10/11); reads filter it out.</summary>
    public const string Deleted = "deleted";
}

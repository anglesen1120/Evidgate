namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The ambient tenant scope for the current async flow (00 §5.5 C3): handlers,
/// the EF Core factory and the Dapper path read it instead of having the scope
/// passed down through every call. One scope per request/task, so
/// <see cref="Require"/> throws rather than handing out a second one.
/// </summary>
public interface ITenantScopeAccessor
{
    /// <summary>The scope of the current async flow, or null when there is none (or it was disposed).</summary>
    ITenantScope? Current { get; }

    /// <summary>
    /// The active scope of the current async flow. Throws
    /// <see cref="TenantScopeMissingException"/> when there is none, or when the
    /// one that was opened has already committed or been disposed.
    /// </summary>
    ITenantScope Require();
}

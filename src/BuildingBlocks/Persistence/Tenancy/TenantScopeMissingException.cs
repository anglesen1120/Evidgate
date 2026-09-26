namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Thrown when tenant-scoped SQL is about to run without an active
/// <see cref="ITenantScope"/> (00 §5.5 C3, 01.4.2-T1.5 / AC1). This is a bug
/// guard, not a domain error: every request and task opens exactly one scope
/// before its first statement, so a caller that reaches the database anyway
/// would otherwise run with no <c>evidgate.org_id</c> — zero rows under RLS, or
/// a write into the wrong tenant's world.
/// </summary>
public sealed class TenantScopeMissingException : InvalidOperationException
{
    /// <summary>Message used when the failing path is not named by the caller.</summary>
    public const string DefaultMessage =
        "No active tenant scope: call ITenantScopeFactory.BeginAsync(orgId, projectId?) before issuing SQL (00 §5.4.2, §5.5 C3).";

    /// <summary>Creates the exception with <see cref="DefaultMessage"/>.</summary>
    public TenantScopeMissingException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception with a caller-supplied message describing the failing path.</summary>
    public TenantScopeMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a caller-supplied message and the underlying failure.</summary>
    public TenantScopeMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

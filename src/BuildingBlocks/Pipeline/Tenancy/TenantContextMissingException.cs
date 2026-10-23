namespace Evidgate.BuildingBlocks.Pipeline.Tenancy;

/// <summary>
/// A pipeline-managed command or query was dispatched without a resolved request
/// tenant (00 §5.5 C2): the tenant transaction behaviour fails closed rather than
/// running the handler unscoped — and never picks a default organization. The
/// host's envelope middleware maps it to the 00 §9.2 <c>401 unauthenticated</c>
/// envelope, since a request with no resolved principal is unauthenticated.
/// </summary>
public sealed class TenantContextMissingException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public TenantContextMissingException()
        : base(
            "No request tenant context is set for the current async flow: the host must "
            + "resolve the principal's organization before dispatching through the C2 pipeline (00 §5.5 C2).")
    {
    }
}

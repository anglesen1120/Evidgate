namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// One suite member that depends on another through <c>case_dependency</c> (05.3.3-T2.2). A member is identified by the pair a
/// planned execution also carries, so Orchestrator maps it to its own rows without reading Catalog tables.
/// </summary>
/// <param name="CaseRevisionId">The member's test revision.</param>
/// <param name="VariantHash">The member's variant hash, <c>sha256:&lt;64 hex&gt;</c>.</param>
public sealed record DependantMember(Guid CaseRevisionId, string VariantHash);

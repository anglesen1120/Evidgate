namespace Evidgate.BuildingBlocks.SharedKernel.Modules;

/// <summary>
/// Canonical module names of 00 §5.1 (the suffix of
/// <c>Evidgate.Modules.&lt;Name&gt;</c>). They are the tag values used by the
/// <c>evidgate.module</c> span/metric tag (01.1.2-T1.2) so a trace or a series
/// names the same module as the project tree.
/// </summary>
public static class ModuleName
{
    public const string Identity = "identity";
    public const string Tenancy = "tenancy";
    public const string Catalog = "catalog";
    public const string Authoring = "authoring";
    public const string Orchestrator = "orchestrator";
    public const string Ingestion = "ingestion";
    public const string Evidence = "evidence";
    public const string Policy = "policy";
    public const string Healing = "healing";
    public const string Usage = "usage";
    public const string Audit = "audit";
    public const string Notifications = "notifications";
}

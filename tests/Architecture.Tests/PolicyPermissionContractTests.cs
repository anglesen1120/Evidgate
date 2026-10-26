using System.Text.RegularExpressions;
using Architecture.Tests.Rules;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.7 S2 / T51 (07.1.1-T3.1 done-when, extended by 13.1.1-T2.4): the permission a handler declares in the
/// Mediator pipeline is the <c>x-permission</c> its OpenAPI operation documents. The spec is read as text (the same
/// line-based approach <c>PermissionCatalogueTests</c> uses), and the handlers are found by name in their module
/// assembly, so a namespace move inside a module does not break this test's compile.
/// </summary>
public sealed class PolicyPermissionContractTests
{
    /// <summary>Each operation, the module assembly holding its handler, and the permission both must carry.</summary>
    private static readonly (string Module, string OperationId, string HandlerName, string Permission)[] Operations =
    [
        ("Policy", "createPolicyRevision", "CreatePolicyRevisionCommandHandler", "policy:write"),
        ("Policy", "activatePolicyRevision", "ActivatePolicyCommandHandler", "policy:write"),
        ("Policy", "getPolicyRevision", "GetPolicyRevisionQueryHandler", "policy:read"),
        ("Policy", "listPolicyRevisions", "ListPolicyRevisionsQueryHandler", "policy:read"),
        ("Policy", "createQuarantineEntry", "CreateQuarantineEntryCommandHandler", "quarantine:write"),
        ("Policy", "removeQuarantineEntry", "RemoveQuarantineEntryCommandHandler", "quarantine:write"),
        ("Policy", "listQuarantineEntries", "ListQuarantineEntriesQueryHandler", "policy:read"),

        // 13.1.1-T2.4: the sixteen authoring operations of 13.1.1-T2 (13.1.1 §5) — four reads on authoring:read,
        // twelve writes on authoring:write (the split §5's permission column states).
        ("Authoring", "listAuthoringTree", "GetAuthoringTreeQueryHandler", "authoring:read"),
        ("Authoring", "createAuthoringNode", "CreateNodeCommandHandler", "authoring:write"),
        ("Authoring", "updateAuthoringNode", "UpdateNodeCommandHandler", "authoring:write"),
        ("Authoring", "reorderAuthoringNodes", "ReorderNodesCommandHandler", "authoring:write"),
        ("Authoring", "deleteAuthoringNode", "DeleteNodeCommandHandler", "authoring:write"),
        ("Authoring", "listChecklistItems", "ListChecklistItemsQueryHandler", "authoring:read"),
        ("Authoring", "createChecklistItem", "CreateChecklistItemCommandHandler", "authoring:write"),
        ("Authoring", "updateChecklistItem", "UpdateChecklistItemCommandHandler", "authoring:write"),
        ("Authoring", "deleteChecklistItem", "DeleteChecklistItemCommandHandler", "authoring:write"),
        ("Authoring", "listManualCases", "ListManualCasesQueryHandler", "authoring:read"),
        ("Authoring", "createManualCase", "CreateManualCaseCommandHandler", "authoring:write"),
        ("Authoring", "getManualCase", "GetManualCaseQueryHandler", "authoring:read"),
        ("Authoring", "updateManualCase", "UpdateManualCaseCommandHandler", "authoring:write"),
        ("Authoring", "deleteManualCase", "DeleteManualCaseCommandHandler", "authoring:write"),
        ("Authoring", "setManualCaseState", "SetManualCaseStateCommandHandler", "authoring:write"),
        ("Authoring", "moveManualCase", "MoveManualTestCaseCommandHandler", "authoring:write"),

        // 13.1.1-T3: the version timeline (routes 22, 23) on authoring:read and the four restore routes
        // (6, 11, 18, 24) on authoring:write — the split §5's permission column states.
        ("Authoring", "listAuthoringVersions", "ListAuthoringVersionsQueryHandler", "authoring:read"),
        ("Authoring", "getAuthoringVersion", "GetAuthoringVersionQueryHandler", "authoring:read"),
        ("Authoring", "restoreAuthoringNode", "RestoreNodeCommandHandler", "authoring:write"),
        ("Authoring", "restoreChecklistItem", "RestoreChecklistItemCommandHandler", "authoring:write"),
        ("Authoring", "restoreManualCase", "RestoreManualCaseCommandHandler", "authoring:write"),
        ("Authoring", "restoreAuthoringVersion", "RestoreVersionCommandHandler", "authoring:write"),

        // 13.1.1-T5: route 25, the import/export pair — the import is a write on its own command, the export a read.
        ("Authoring", "importAuthoring", "ImportAuthoringCommandHandler", "authoring:write"),
        ("Authoring", "exportAuthoring", "ExportAuthoringQueryHandler", "authoring:read"),
    ];

    [Fact]
    public void Handlers_Declare_The_OpenApi_Permission()
    {
        var specPermissions = SpecPermissionsByOperationId();
        var modules = SolutionLayout.EvidgateAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Evidgate.Modules.", StringComparison.Ordinal) == true)
            .ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

        foreach (var (module, operationId, handlerName, permission) in Operations)
        {
            specPermissions.ContainsKey(operationId).ShouldBeTrue($"the spec must document the {operationId} operation");
            specPermissions[operationId].ShouldBe(permission, $"{operationId} must declare x-permission {permission}");

            modules.ShouldContainKey($"Evidgate.Modules.{module}");
            var types = ArchitectureRules.SafeGetTypes(modules[$"Evidgate.Modules.{module}"]);
            var handler = types.SingleOrDefault(type => type.Name == handlerName);
            handler.ShouldNotBeNull($"the {module} assembly must declare {handlerName}");
            DeclaredPermission(handler!).ShouldBe(permission, $"{handlerName} must declare the operation's x-permission");
        }
    }

    /// <summary>The permission a handler class declares via <c>[Permission]</c>, read without instantiating it.</summary>
    private static string? DeclaredPermission(Type handler)
    {
        var attribute = handler
            .GetCustomAttributesData()
            .SingleOrDefault(a => a.AttributeType.FullName == typeof(PermissionAttribute).FullName);

        if (attribute is null || attribute.ConstructorArguments.Count != 1)
        {
            return null;
        }

        return attribute.ConstructorArguments[0].Value as string;
    }

    /// <summary>
    /// Every <c>operationId</c> in <c>contracts/openapi.yaml</c> mapped to the <c>x-permission</c> of its
    /// operation. The reader is a small indentation-aware scan: path items sit at two spaces, methods at four, and
    /// the operation's own fields at six.
    /// </summary>
    private static Dictionary<string, string> SpecPermissionsByOperationId()
    {
        var path = Path.Combine(SolutionLayout.RepoRoot, "contracts", "openapi.yaml");
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        string? operationId = null;
        string? permission = null;

        void Flush()
        {
            if (operationId is not null && permission is not null)
            {
                permissions[operationId] = permission;
            }

            operationId = null;
            permission = null;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (Regex.IsMatch(line, @"^  /\S"))
            {
                Flush();
                continue;
            }

            if (Regex.IsMatch(line, @"^    (get|post|put|patch|delete|head|options):"))
            {
                Flush();
                continue;
            }

            var operation = Regex.Match(line, @"^\s+operationId:\s*(?<value>\S+)\s*$");
            if (operation.Success)
            {
                operationId = operation.Groups["value"].Value;
                continue;
            }

            var declared = Regex.Match(line, @"^\s+x-permission:\s*(?<value>\S+)\s*$");
            if (declared.Success)
            {
                permission = declared.Groups["value"].Value;
            }
        }

        Flush();
        return permissions;
    }
}

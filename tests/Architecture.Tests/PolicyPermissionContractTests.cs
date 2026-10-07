using System.Text.RegularExpressions;
using Architecture.Tests.Rules;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.7 S2 / T51 (07.1.1-T3.1 done-when): the permission a policy handler declares in the Mediator pipeline is
/// the <c>x-permission</c> its OpenAPI operation documents. The spec is read as text (the same line-based
/// approach <c>PermissionCatalogueTests</c> uses), and the handlers are found by name in the Policy assembly, so a
/// namespace move inside the module does not break this test's compile.
/// </summary>
public sealed class PolicyPermissionContractTests
{
    /// <summary>Each policy operation, the handler that serves it, and the permission both must carry.</summary>
    private static readonly (string OperationId, string HandlerName, string Permission)[] Operations =
    [
        ("createPolicyRevision", "CreatePolicyRevisionCommandHandler", "policy:write"),
        ("getPolicyRevision", "GetPolicyRevisionQueryHandler", "policy:read"),
        ("listPolicyRevisions", "ListPolicyRevisionsQueryHandler", "policy:read"),
    ];

    [Fact]
    public void Policy_Handlers_Declare_The_OpenApi_Permission()
    {
        var specPermissions = SpecPermissionsByOperationId();
        var policy = SolutionLayout.EvidgateAssemblies().Single(assembly => assembly.GetName().Name == "Evidgate.Modules.Policy");
        var types = ArchitectureRules.SafeGetTypes(policy);

        foreach (var (operationId, handlerName, permission) in Operations)
        {
            specPermissions.ContainsKey(operationId).ShouldBeTrue($"the spec must document the {operationId} operation");
            specPermissions[operationId].ShouldBe(permission, $"{operationId} must declare x-permission {permission}");

            var handler = types.SingleOrDefault(type => type.Name == handlerName);
            handler.ShouldNotBeNull($"the Policy assembly must declare {handlerName}");
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

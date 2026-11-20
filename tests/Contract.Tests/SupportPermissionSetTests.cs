using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The support-access permission set of <c>contracts/permissions.json</c>, pinned: who may file a
/// support grant (staff only), who may approve one (a tenant admin) and who may read them.
/// </summary>
public sealed class SupportPermissionSetTests
{
    [Fact]
    public void PermissionsJson_SupportSet()
    {
        var catalogue = JsonNode.Parse(
            File.ReadAllText(Path.Combine(ContractPaths.ContractsDirectory, "permissions.json")))!.AsObject();

        var support = catalogue["permissions"]!.AsArray()
            .Select(node => node!.AsObject())
            .Where(node => node["code"]!.GetValue<string>().StartsWith("support:", StringComparison.Ordinal))
            .ToDictionary(node => node["code"]!.GetValue<string>(), StringComparer.Ordinal);

        support.Keys.ShouldBe(["support:approve", "support:grant", "support:read"], ignoreOrder: true);

        // Filing a grant is staff-only; approving is the tenant's own act; both are sensitive.
        Flag(support["support:grant"], "tenant_assignable").ShouldBeFalse();
        Flag(support["support:grant"], "sensitive").ShouldBeTrue();
        Flag(support["support:approve"], "tenant_assignable").ShouldBeTrue();
        Flag(support["support:approve"], "sensitive").ShouldBeTrue();
        Flag(support["support:read"], "sensitive").ShouldBeFalse();
        support.Values.ShouldAllBe(node => !Flag(node, "reserved"));

        var holders = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var role in catalogue["roles"]!.AsArray().Select(node => node!.AsObject()))
        {
            foreach (var code in role["permissions"]!.AsArray().Select(node => node!.GetValue<string>()))
            {
                if (support.ContainsKey(code))
                {
                    holders[code] = [.. holders.GetValueOrDefault(code, []), role["code"]!.GetValue<string>()];
                }
            }
        }

        holders["support:grant"].ShouldBe(["platform_operator"]);
        holders["support:approve"].ShouldBe(["tenant_admin"]);
        holders["support:read"].ShouldBe(["platform_operator", "tenant_admin"], ignoreOrder: true);
    }

    private static bool Flag(JsonObject node, string name) => node[name]!.GetValue<bool>();
}

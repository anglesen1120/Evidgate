using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Corvus.Json;
using Corvus.Json.Validator;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 02.2.1-T1.1: <c>contracts/permissions.json</c> is the single source of every
/// permission string (00 §2, 02 D2), so its own shape and its coverage of the
/// documented surface are asserted here — with the schema file next to it and the
/// cross-field rules a JSON Schema cannot express.
/// </summary>
/// <remarks>
/// The file deliberately does NOT live in <c>contracts/schemas/</c>,
/// <c>contracts/adapter/</c> or <c>contracts/manifest/</c>: both runtime loaders
/// (<c>ContractSchemaSet</c> and the CLI's Ajv module) compile every <c>*.json</c>
/// under those three roots, and this is a
/// build-time catalogue, not a request body. The generator
/// (<c>scripts/generate-permissions.py</c>) and the drift gate
/// (<c>scripts/check-permissions-drift.sh</c>) consume the same file.
/// </remarks>
public sealed class PermissionCatalogueTests
{
    /// <summary>The nine built-in roles of 00 §2 — no role is added or removed here.</summary>
    private static readonly string[] BuiltInRoles =
    [
        "agent", "billing_operator", "ci_producer", "platform_operator", "qa_engineer",
        "release_approver", "tenant_admin", "test_maintainer", "viewer",
    ];

    /// <summary>
    /// The <c>reserved</c> set, pinned: a string is reserved when its first consumer is a
    /// V2/V3 story (00 §9.3's V2 tag, 02.5, 02.3.3, 03 §4.5's V2 drafts, T59's V2 agent
    /// pools) — V1 has no endpoint or handler for it. AC2's drift test (02.2.1-T3) reads
    /// this flag, so a change here is a deliberate contract edit.
    /// </summary>
    private static readonly string[] Reserved =
    [
        "agent_pool:read", "agent_pool:write", "ai_draft:request", "draft:dry_run",
        "draft:merge_request", "draft:write", "healing:approve", "healing:revoke",
        "job:claim", "job:cleanup", "job:execute", "service_identity:read",
        "service_identity:write", "sso:read", "sso:write",
    ];

    /// <summary>
    /// T79: the codes a member principal must never hold — the staff-only ones
    /// (<c>support:grant</c>, <c>billing:*</c>) and the service-only ones
    /// (<c>job:claim</c>/<c>job:execute</c> are agent-only, <c>event:write</c> is
    /// producer-only).
    /// </summary>
    private static readonly string[] NonAssignable =
        ["billing:read", "billing:write", "event:write", "job:claim", "job:execute", "support:grant"];

    /// <summary>
    /// Every <c>x-permission</c> the spec declares is a catalogue code, or one of the two
    /// reserved literals: <c>none</c> (no caller resolved, e.g. the token exchange) and
    /// <c>authenticated</c> (any signed-in user, no tenant permission yet — only
    /// <c>POST /v1/orgs</c>, 02.1.1 §5).
    /// </summary>
    [Fact]
    public void Every_Spec_Permission_Is_In_The_Catalogue()
    {
        var catalogue = JsonNode.Parse(CatalogueJson())!.AsObject();
        var codes = catalogue["permissions"]!.AsArray()
            .Select(entry => entry!["code"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        var declared = SpecPermissions();

        // Non-vacuity: the scan must have seen the spec's permission lines. The scan is
        // line-based because the spec's path items use folded block scalars the
        // test-side reader rejects by design (see ServedDocumentTests); a style change
        // that hides the lines fails here rather than passing silently.
        Assert.Contains("run:create", declared);
        Assert.Contains("healing:triage", declared);
        Assert.Contains("role:read", declared);
        Assert.Contains("none", declared);
        Assert.Contains("authenticated", declared);
        Assert.True(declared.Count > 25, "the spec declares one x-permission per operation");

        var missing = declared
            .Where(permission => permission is not ("none" or "authenticated") && !codes.Contains(permission))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>The catalogue validates against its own schema — and the schema is not vacuous.</summary>
    [Fact]
    public void Catalogue_Validates_Against_Its_Schema()
    {
        Assert.Empty(Validate(CatalogueJson()));

        // Non-vacuity, one mutation per schema rule the file relies on: a missing flag,
        // a malformed code and an unknown extra member must all be rejected.
        foreach (var mutated in new[]
        {
            """{"version":1,"comment":"c","roles":[{"code":"viewer","name":"V","kind":"member","description":"d","permissions":["run:read"]}],"permissions":[{"code":"run:read","resource":"run","action":"read","description":"d","sensitive":false,"reserved":false}]}""",
            """{"version":1,"comment":"c","roles":[{"code":"viewer","name":"V","kind":"member","description":"d","permissions":["RUN READ"]}],"permissions":[{"code":"RUN READ","resource":"run","action":"read","description":"d","sensitive":false,"reserved":false,"tenant_assignable":true}]}""",
            """{"version":1,"comment":"c","roles":[{"code":"viewer","name":"V","kind":"member","description":"d","permissions":["run:read"]}],"permissions":[{"code":"run:read","resource":"run","action":"read","description":"d","sensitive":false,"reserved":false,"tenant_assignable":true,"extra":1}]}""",
        })
        {
            Assert.NotEmpty(Validate(mutated));
        }
    }

    /// <summary>
    /// The cross-field rules: <c>code</c> is exactly <c>resource:action</c>, codes are
    /// unique, every role's grants exist, and the role set is the nine 00 §2 built-ins.
    /// </summary>
    [Fact]
    public void Catalogue_Is_Internally_Consistent()
    {
        var catalogue = JsonNode.Parse(CatalogueJson())!.AsObject();
        var permissions = catalogue["permissions"]!.AsArray();

        var codes = new List<string>();
        foreach (var node in permissions)
        {
            var entry = node!.AsObject();
            var code = entry["code"]!.GetValue<string>();
            var resource = entry["resource"]!.GetValue<string>();
            var action = entry["action"]!.GetValue<string>();

            Assert.Equal($"{resource}:{action}", code);
            Assert.False(string.IsNullOrWhiteSpace(entry["description"]!.GetValue<string>()));
            codes.Add(code);
        }

        Assert.Equal(codes.Distinct(StringComparer.Ordinal), codes);

        var roles = catalogue["roles"]!.AsArray();
        Assert.Equal(
            BuiltInRoles.Order(StringComparer.Ordinal),
            roles.Select(role => role!["code"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        foreach (var role in roles)
        {
            var grants = role!["permissions"]!.AsArray().Select(grant => grant!.GetValue<string>()).ToList();

            Assert.NotEmpty(grants);
            Assert.DoesNotContain(grants, grant => !codes.Contains(grant, StringComparer.Ordinal));
        }

        // AC2's reserved flag: pinned, so the "referenced or reserved" rule has a fixed
        // starting point.
        Assert.Equal(
            Reserved.Order(StringComparer.Ordinal),
            permissions
                .Where(entry => entry!["reserved"]!.GetValue<bool>())
                .Select(entry => entry!["code"]!.GetValue<string>())
                .Order(StringComparer.Ordinal));

        // T79: the non-assignable codes are the staff/service-only ones.
        Assert.Equal(
            NonAssignable.Order(StringComparer.Ordinal),
            permissions
                .Where(entry => !entry!["tenant_assignable"]!.GetValue<bool>())
                .Select(entry => entry!["code"]!.GetValue<string>())
                .Order(StringComparer.Ordinal));
    }

    /// <summary>Validates an instance against the catalogue schema; returns one row per failure.</summary>
    private static string[] Validate(string instanceJson)
    {
        const string CanonicalUri = "urn:evidgate:contract:permissions-schema:v1";
        var schemaJson = File.ReadAllText(
            Path.Combine(ContractPaths.ContractsDirectory, "permissions.schema.json"));

        var schema = JsonSchema.FromText(
            schemaJson,
            CanonicalUri,
            new JsonSchema.Options(allowFileSystemAndHttpResolution: false, alwaysAssertFormat: true));

        using var instance = JsonDocument.Parse(instanceJson);

        return
        [
            .. schema.Validate(instance.RootElement, ValidationLevel.Detailed).Results
                .Where(result => !result.Valid)
                .Select(result => $"{(result.Location is { } location ? location.Item3.ToString() : string.Empty)} {result.Message}".Trim()),
        ];
    }

    /// <summary>The repository's catalogue file, verbatim.</summary>
    private static string CatalogueJson() =>
        File.ReadAllText(Path.Combine(ContractPaths.ContractsDirectory, "permissions.json"));

    /// <summary>
    /// Every <c>x-permission:</c> value in <c>contracts/openapi.yaml</c>, read as lines:
    /// the spec declares each one as its own plain scalar, and the test-side YAML reader
    /// deliberately refuses the folded block scalars used elsewhere in a path item.
    /// </summary>
    private static IReadOnlyList<string> SpecPermissions()
    {
        var pattern = new Regex(@"^\s+x-permission:\s*(?<value>[A-Za-z_]+:[A-Za-z_]+|none|authenticated)\s*$", RegexOptions.Compiled);
        var path = Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml");

        return
        [
            .. File.ReadLines(path)
                .Select(line => pattern.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["value"].Value),
        ];
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// AC7 (02.3.1 §9:101) and FR-02-3: the login contract has no password surface at
/// all. V1 authenticates a human through a provider only, so neither the served
/// spec (<c>contracts/openapi.yaml</c>) nor a wire schema under
/// <c>contracts/schemas</c> may carry a password path segment, a password-typed
/// parameter or a password-named property — the test fails the moment one is
/// added, whether it is a route or a field.
/// </summary>
/// <remarks>
/// <para>
/// The check is a text scan, not a schema walk: AC7 calls for a grep-able contract
/// test, and no YAML library is referenced by this project, so
/// <see cref="ContractPasswordScan"/> reads the spec line by line (an indented key
/// stack that skips the prose of <c>description</c> block scalars) and the JSON
/// schemas through <see cref="JsonDocument"/>. Nothing about the production code
/// is asserted here — the point is the shipped artifact a client and a reviewer
/// read.
/// </para>
/// <para>
/// The negative control in <see cref="The_Scan_Detects_A_Seeded_Password_Path_And_Field"/>
/// is what keeps this test from passing vacuously: a synthetic document carrying
/// both a password path and a password member must be reported, and a clean one
/// must not.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ContractTests
{
    /// <summary>The repository root, through this assembly's existing locator (<c>PermissionCatalogueFile.RepoRoot()</c>).</summary>
    private static string RepoRoot => PermissionCatalogueFile.RepoRoot();

    /// <summary>AC7: no path or property of the contract may name a password or a credential.</summary>
    [Fact]
    public void NoPasswordPath()
    {
        var contracts = Path.Combine(RepoRoot, "contracts");

        var spec = ContractPasswordScan.OpenApi(File.ReadAllText(Path.Combine(contracts, "openapi.yaml")));

        var schemaFiles = Directory
            .EnumerateFiles(Path.Combine(contracts, "schemas"), "*.json")
            .Order(StringComparer.Ordinal)
            .ToList();

        schemaFiles.ShouldNotBeEmpty("contracts/schemas must hold the wire schemas AC7 covers");
        var schemas = schemaFiles
            .Select(path => (Path: path, Scan: ContractPasswordScan.Schema(File.ReadAllText(path))))
            .ToList();

        var findings = spec.Findings
            .Select(finding => "contracts/openapi.yaml: " + finding)
            .Concat(schemas.SelectMany(
                schema => schema.Scan.Findings.Select(
                    finding => $"contracts/schemas/{Path.GetFileName(schema.Path)}: {finding}")))
            .ToList();

        findings.ShouldBeEmpty(
            "AC7/FR-02-3: the login contract has no password field, path or credential member — "
            + "the only credential the platform accepts is the provider's:\n"
            + string.Join('\n', findings));

        // Non-vacuity: the scan read the real documents. Both login routes are in
        // the path table, the table and its member set are the size the spec has,
        // and the schema walk reached every property of every schema file. The
        // spec's path keys are relative to `servers[0].url` (`/v1`), the same
        // prefix ServedRouteTable adds, so they are asserted as the file writes them.
        spec.PathTemplates
            .Where(template => template == "/auth/oidc/{provider}/start")
            .ShouldNotBeEmpty("the scan must have read the served path table, not an empty file");
        spec.PathTemplates
            .Where(template => template == "/auth/oidc/{provider}/callback")
            .ShouldNotBeEmpty("the callback is the route AC7 is about");
        spec.PathTemplates.Count.ShouldBeGreaterThan(40, "the whole path table is scanned, not a prefix of it");
        spec.MemberNames.Count.ShouldBeGreaterThan(500, "every mapping key of the spec is a member the deny-list sees");

        schemas.Count.ShouldBeGreaterThanOrEqualTo(4, "every schema file under contracts/schemas is scanned");
        schemas
            .Where(schema => schema.Scan.PropertyNames.Count == 0)
            .Select(schema => Path.GetFileName(schema.Path))
            .ShouldBeEmpty("every schema file must yield the properties the deny-list judges, or its scan is vacuous");
        schemas.Sum(schema => schema.Scan.PropertyNames.Count)
            .ShouldBeGreaterThan(10, "the schema walk must reach the properties it judges");
    }

    /// <summary>
    /// The negative control of <see cref="NoPasswordPath"/>: a synthetic document
    /// with a password path segment, a password-named parameter and a password
    /// property must be reported, and a document without one must stay clean — so
    /// the deny-list cannot pass by never firing.
    /// </summary>
    [Fact]
    public void The_Scan_Detects_A_Seeded_Password_Path_And_Field()
    {
        const string seeded =
            """
            paths:
              /v1/auth/password/reset:
                post:
                  parameters:
                    - name: oldPasswd
                      in: query
            """;

        var report = ContractPasswordScan.OpenApi(seeded);

        report.PathTemplates.ShouldHaveSingleItem().ShouldBe("/v1/auth/password/reset");
        report.Findings
            .Where(finding => finding.Contains("'/v1/auth/password/reset'", StringComparison.Ordinal))
            .ShouldNotBeEmpty("a password path segment must be reported");
        report.Findings
            .Where(finding => finding.Contains("oldPasswd", StringComparison.Ordinal))
            .ShouldNotBeEmpty("a password-named parameter must be reported");

        // …and the same scanner stays quiet on a compliant document.
        const string clean =
            """
            paths:
              /v1/auth/oidc/{provider}/start:
                get:
                  x-permission: none
                  description: >-
                    The prose may mention a password without declaring one, which is
                    why block scalars are skipped: pass word here is not a field.
                  parameters:
                    - name: returnUrl
                      in: query
            """;

        ContractPasswordScan.OpenApi(clean).Findings.ShouldBeEmpty();

        var seededSchema = ContractPasswordScan.Schema(
            """{"properties":{"password":{"type":"string"},"user":{"properties":{"passwd":{"type":"string"}}}}}""");

        seededSchema.Findings
            .Where(finding => finding.Contains("'password'", StringComparison.Ordinal))
            .ShouldNotBeEmpty("a password property must be reported");
        seededSchema.Findings
            .Where(finding => finding.Contains("'passwd'", StringComparison.Ordinal))
            .ShouldNotBeEmpty("a nested password property must be reported too, not only a top-level one");

        ContractPasswordScan.Schema("""{"properties":{"email":{"type":"string"}}}""")
            .Findings.ShouldBeEmpty();
    }
}

/// <summary>
/// The AC7 deny-list as a pure function over a contract document: the path
/// templates of <c>contracts/openapi.yaml</c>, every mapping key it declares, and
/// the property names of a JSON schema.
/// </summary>
/// <param name="Findings">One line per violation, empty for a clean document.</param>
/// <param name="PathTemplates">The path keys the scan read (empty for a schema).</param>
/// <param name="MemberNames">The mapping keys the scan read (empty for a schema).</param>
/// <param name="PropertyNames">The schema property names the scan read (empty for a spec).</param>
internal sealed record PasswordScan(
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> PathTemplates,
    IReadOnlyList<string> MemberNames,
    IReadOnlyList<string> PropertyNames);

/// <summary>
/// The AC7 scan itself, deliberately dependency-free: this project references no
/// YAML parser, and the rule is a name rule, so a line scan of the spec is enough
/// and reads the same file a reviewer greps.
/// </summary>
internal static class ContractPasswordScan
{
    /// <summary>The path segments a login contract may never carry (AC7).</summary>
    private static readonly string[] ForbiddenPathSegments = ["password", "passwd", "credential"];

    /// <summary>The member names that mean a password credential (FR-02-3).</summary>
    private static readonly Regex PasswordMember = new(
        "pass(word|wd)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Scans <c>contracts/openapi.yaml</c>: path segments, mapping keys and parameter names.</summary>
    /// <param name="text">The spec's text.</param>
    internal static PasswordScan OpenApi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var findings = new List<string>();
        var paths = new List<string>();
        var members = new List<string>();

        // The indented key stack: only used (a) to pop back to a parent level and
        // (b) to skip the body of a `description: >-` block scalar, whose prose may
        // contain any word without declaring a field.
        var containers = new List<(int Indent, bool Scalar)>();

        foreach (var line in text.Split('\n'))
        {
            var content = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var trimmed = content.TrimStart(' ');
            var indent = content.Length - trimmed.Length;

            if (containers.Count > 0 && containers[^1].Scalar && indent > containers[^1].Indent)
            {
                continue;
            }

            if (trimmed[0] == '#')
            {
                continue;
            }

            // A sequence item ("- name: returnUrl") carries its key two columns in,
            // so the key's own level is the dash's plus two.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                indent += 2;
                trimmed = trimmed[2..].TrimStart(' ');
            }

            // A key ends at the first colon followed by a space or the line end: a custom-method path
            // such as `/x/changes:apply:` carries a colon inside the key itself, so splitting at the
            // first colon found would record a truncated path.
            var colon = -1;
            for (var at = trimmed.IndexOf(':'); at >= 0; at = trimmed.IndexOf(':', at + 1))
            {
                if (at + 1 >= trimmed.Length || trimmed[at + 1] == ' ')
                {
                    colon = at;
                    break;
                }
            }

            if (colon <= 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim();
            var rest = trimmed[(colon + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            while (containers.Count > 0 && containers[^1].Indent >= indent)
            {
                containers.RemoveAt(containers.Count - 1);
            }

            if (key[0] == '/')
            {
                paths.Add(key);
                foreach (var segment in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ForbiddenPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
                    {
                        findings.Add($"path '{key}' carries the segment '{segment}'");
                    }
                }
            }
            else
            {
                members.Add(key);
                AddNameFinding(findings, key);

                // A parameter's own name is a value, not a key: `- name: passwd` must
                // be reported even though its key is the harmless word `name`.
                if (string.Equals(key, "name", StringComparison.Ordinal))
                {
                    AddNameFinding(findings, Unquote(rest));
                }
            }

            containers.Add((indent, rest.Length > 0 && (rest[0] == '|' || rest[0] == '>')));
        }

        return new PasswordScan(findings, paths, members, []);
    }

    /// <summary>Scans a JSON schema: every property name under any <c>properties</c> or <c>patternProperties</c>.</summary>
    /// <param name="text">The schema's text.</param>
    internal static PasswordScan Schema(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var findings = new List<string>();
        var properties = new List<string>();

        using var document = JsonDocument.Parse(text);
        Walk(document.RootElement, "$", findings, properties);

        return new PasswordScan(findings, [], [], properties);
    }

    private static void Walk(JsonElement element, string at, List<string> findings, List<string> properties)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var isPropertyBag = property.NameEquals("properties") || property.NameEquals("patternProperties");
                    if (isPropertyBag && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var member in property.Value.EnumerateObject())
                        {
                            properties.Add(member.Name);
                            if (PasswordMember.IsMatch(member.Name))
                            {
                                findings.Add($"{at}.{property.Name}: property '{member.Name}' names a password");
                            }
                        }
                    }

                    Walk(property.Value, at + "." + property.Name, findings, properties);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{at}[{index++}]", findings, properties);
                }

                break;
        }
    }

    private static void AddNameFinding(List<string> findings, string name)
    {
        if (name.Length > 0 && PasswordMember.IsMatch(name))
        {
            findings.Add($"member '{name}' names a password");
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2
        && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'))
            ? value[1..^1]
            : value;
}

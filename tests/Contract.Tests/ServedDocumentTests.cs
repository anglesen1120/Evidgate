using System.Text.Json;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 01.3.2-T3.2 / AC4: the OpenAPI document <c>Microsoft.AspNetCore.OpenApi</c>
/// serves is diffed — normalised — against <c>contracts/openapi.yaml</c>, so a
/// route or DTO the spec does not declare fails the build instead of shipping.
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠️ 3.1 emission confirmed</b> (area plan §3, this task's core question):
/// on 2026-09-27 the served document declares <c>"openapi": "3.1.1"</c> while the
/// spec declares <c>3.1.0</c> — the same 3.1 dialect, the patch level owned by
/// <c>Microsoft.OpenApi</c> 2.12.0 (which
/// <c>Microsoft.AspNetCore.OpenApi</c> 10.0.12 pins). The diff therefore
/// normalises the version to major.minor, so the response validation of AC4 stays
/// committed rather than falling back.
/// </para>
/// <para>
/// <b>What the normalised diff covers today.</b> The served document carries the
/// documented routes — <c>/v1/permissions</c> and <c>/v1/roles</c> of 02.2.1-T1.2,
/// whose schemas <c>PermissionListResponse</c>/<c>PermissionView</c>/
/// <c>RoleListResponse</c>/<c>RoleView</c> the spec declares — and no operational or
/// scaffolding surface: the test-only probe route is excluded from the description. The served path
/// keys are mounted route templates while the spec's <c>paths</c> keys are relative
/// to <c>servers[0].url</c> (<c>/v1</c>), so the comparison strips the prefix
/// before diffing. Operation <em>body</em> diffs (parameters, request/response
/// shapes) are still absent: the spec's path items use folded block scalars the
/// test-side reader rejects by design, so the comparable surface stays the dialect
/// line plus the served path and DTO-name sets — asserted below, with non-vacuity
/// checks that the comparison catches an undocumented route and that the prefix
/// strip is what makes a documented route match its spec key.
/// </para>
/// <para>
/// <b>Document-level drift observed but not asserted</b> (it is host configuration
/// rather than a wire shape, and pinning it would pin defaults): the served
/// <c>info</c> is <c>{"title":"Evidgate.Api | v1","version":"1.0.0"}</c> against the
/// spec's <c>Evidgate API</c>/<c>1.0.0</c>, its <c>servers</c> is
/// <c>[{"url":"http://localhost/"}]</c> against the spec's <c>/v1</c>. The
/// only spec schema an implemented response can be validated against is
/// <c>#/components/schemas/ErrorEnvelope</c>, which
/// <see cref="ResponseContractTests"/> does against the live 400/422 bodies.
/// </para>
/// </remarks>
public sealed class ServedDocumentTests
{
    /// <summary>
    /// The scaffolding prefixes no served document may carry: a demo or operational route
    /// belongs under neither mount, and the assertion below fails if one comes back.
    /// </summary>
    private static readonly string[] ForbiddenPathPrefixes = ["/v1/slice", "/internal/slice"];

    /// <summary>
    /// The spec's <c>servers[0].url</c> (00 §9.1: "Servers mount under /v1"). The
    /// served document carries the mounted route template (<c>/v1/permissions</c>)
    /// while the spec's <c>paths</c> keys are relative to this prefix
    /// (<c>/permissions</c>), so the comparison normalises the served side.
    /// </summary>
    private const string ServerPrefix = "/v1";

    [Fact]
    public async Task Served_Document_Is_OpenApi_31()
    {
        var spec = OpenApiSpec.Load();
        using var document = await FetchServedDocumentAsync();

        var served = document.RootElement.GetProperty("openapi").GetString();

        // Served 3.1.1 vs spec 3.1.0 on 2026-09-27: same dialect, library-owned patch.
        Assert.Equal("3.1", MajorMinor(served));
        Assert.Equal("3.1", MajorMinor(spec.Version));

        // Non-vacuity: the normalisation must separate 3.1 from 3.0, or the
        // equality above would pass for a 3.0 document too.
        Assert.NotEqual(MajorMinor(served), MajorMinor("3.0.1"));
    }

    [Fact]
    public async Task Served_Document_Surface_Diff_Against_Spec_Is_Empty()
    {
        var spec = OpenApiSpec.Load();
        using var document = await FetchServedDocumentAsync();
        var servedPaths = MemberNames(document.RootElement.GetProperty("paths"))
            .Select(SpecRelative)
            .ToList();
        var servedSchemas = document.RootElement.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemas)
                ? MemberNames(schemas)
                : [];

        // Every route and DTO the host serves is in the spec, and nothing is excused.
        Assert.Empty(Unexplained(servedPaths, spec.PathKeys, []));
        Assert.Empty(Unexplained(servedSchemas, spec.SchemaNames, []));

        // The prefix normalisation itself: the strip must be what makes a real route
        // match its spec key, or the two assertions above would pass vacuously for a
        // host that served no documented route at all.
        Assert.Equal("/permissions", SpecRelative("/v1/permissions"));
        Assert.Equal("/roles", SpecRelative("/v1/roles"));
        Assert.Equal("/unmounted", SpecRelative("/unmounted"));
        Assert.Contains("/permissions", servedPaths);
        Assert.Contains("/roles", servedPaths);

        // Non-vacuity: the same comparison fails on an undocumented route or DTO.
        Assert.Equal(["/undocumented"], Unexplained(["/undocumented"], spec.PathKeys, []));
        Assert.Equal(["UndocumentedDto"], Unexplained(["UndocumentedDto"], spec.SchemaNames, []));
    }

    /// <summary>
    /// A demo surface must never reach production again: the served document carries no
    /// path under the scaffolding prefixes, on either mount.
    /// </summary>
    [Fact]
    public async Task Served_Document_Carries_No_Scaffolding_Route()
    {
        using var document = await FetchServedDocumentAsync();

        var paths = MemberNames(document.RootElement.GetProperty("paths")).ToList();
        Assert.NotEmpty(paths);
        Assert.DoesNotContain(
            paths,
            path => ForbiddenPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)));
    }

    /// <summary>Strips the spec's server prefix from a served route template (00 §9.1).</summary>
    private static string SpecRelative(string servedPath) =>
        servedPath.StartsWith(ServerPrefix, StringComparison.Ordinal)
            ? servedPath[ServerPrefix.Length..]
            : servedPath;

    private static async Task<JsonDocument> FetchServedDocumentAsync()
    {
        await using var factory = new EvidgateApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Names the served document carries that the spec (or the recorded allowance) does not.</summary>
    private static List<string> Unexplained(IEnumerable<string> served, IReadOnlyList<string> known, IReadOnlyList<string> allowed)
        => [.. served.Where(name => !known.Contains(name, StringComparer.Ordinal) && !allowed.Contains(name, StringComparer.Ordinal))];

    private static string[] MemberNames(JsonElement mapping)
        => [.. mapping.EnumerateObject().Select(member => member.Name)];

    /// <summary>The OpenAPI dialect of a version string: <c>3.1.1</c> → <c>3.1</c>.</summary>
    private static string MajorMinor(string? version)
    {
        Assert.False(string.IsNullOrEmpty(version), "the document must declare an 'openapi' version");
        var parts = version!.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }
}

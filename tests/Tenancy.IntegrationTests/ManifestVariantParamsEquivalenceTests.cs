using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The upload's variant-parameter refusal is the catalogue normaliser itself: an upload answers 201 exactly when
/// <see cref="ICatalogContracts.NormalizeVariantHashes"/> yields a hash for the same parameters. Rows whose shape the
/// manifest schema refuses first (a key the schema forbids) are checked against the normaliser alone.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ManifestVariantParamsEquivalenceTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ManifestVariantParamsEquivalenceTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>Each row: a name, the params JSON, and whether the normaliser is expected to refuse it.</summary>
    public static IEnumerable<object[]> SharedCorpus()
    {
        yield return Row("ascii_at_limit", SizedParameters(8000), refused: false);
        yield return Row("ascii_one_over", SizedParameters(8001), refused: true);
        yield return Row("ascii_32_by_256", JsonSerializer.Serialize(Parameters(32, 256)), refused: true);
        yield return Row("cjk_at_limit", SizedParameters(8000, cjk: true), refused: false);
        yield return Row("cjk_one_over", SizedParameters(8001, cjk: true), refused: true);
        yield return Row("cjk_6_by_256", JsonSerializer.Serialize(Parameters(6, 256, '\u754c')), refused: true);
        yield return Row("astral_128", AstralParameters(128), refused: false);
        yield return Row("astral_129", AstralParameters(129), refused: true);
        yield return Row("keys_32", JsonSerializer.Serialize(Parameters(32, 1)), refused: false);
        yield return Row("keys_33", JsonSerializer.Serialize(Parameters(33, 1)), refused: true);
        yield return Row("named_non_identifying", NonIdentifyingParameters(), refused: false);
        yield return Row("duplicate_member_33", DuplicateMembers(33), refused: true);
        yield return Row("secret_and_oversize", SecretOversizeParameters(), refused: true);
    }

    /// <summary>The upload corpus plus the shapes the manifest schema rejects before the normaliser runs.</summary>
    public static IEnumerable<object[]> NormalizerCorpus()
    {
        foreach (var row in SharedCorpus())
        {
            yield return row;
        }

        yield return Row("underscore_hint_32", HintParameters(32), refused: false);
        yield return Row("underscore_hint_33", HintParameters(33), refused: true);
    }

    [Theory]
    [MemberData(nameof(SharedCorpus))]
    public async Task Upload_VariantParams_AnswersWhenTheCatalogHasAHash(string name, string parameters, bool refused)
    {
        await using var world = await ManifestTests.StartAsync(_fixture, cases: 1);
        await using var services = world.Api.Services.CreateAsyncScope();
        var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();
        var body = ManifestJson(world.SuiteRevision, world.Cases[0], parameters);

        var answer = await UploadAsync(world.Client, world.Admin, world.Project, body);
        using var document = JsonDocument.Parse(parameters);
        var hash = contracts.NormalizeVariantHashes([document.RootElement.Clone()])[0];

        (hash is null).ShouldBe(refused, name);
        if (hash is null)
        {
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, name);
            answer.ErrorCode.ShouldBe("manifest_invalid", name);
        }
        else
        {
            answer.Status.ShouldBe(HttpStatusCode.Created, name);
        }
    }

    [Theory]
    [MemberData(nameof(NormalizerCorpus))]
    public void NormalizeVariantParams_BoundaryCorpus_MatchesExpectedHashability(string name, string parameters, bool refused)
    {
        using var document = JsonDocument.Parse(parameters);
        var problems = new List<VariantProblem>();

        var normalized = VariantNormalizer.Normalize(document.RootElement, "/params", problems);

        (normalized is null).ShouldBe(refused, name);
        problems.Count.ShouldBe(refused ? 1 : 0, name);
    }

    [Fact]
    public void NormalizeVariantParams_NonIdentifyingValues_AreDroppedBeforeTheSizeBound()
    {
        using var document = JsonDocument.Parse(NonIdentifyingParameters());
        var names = document.RootElement.EnumerateObject().Select(member => member.Name).ToArray();
        names.Length.ShouldBe(12);
        Encoding.UTF8.GetByteCount(document.RootElement.GetRawText()).ShouldBeGreaterThan(8000);
        names.Where(name => name != "browser").ShouldAllBe(name => ParameterSecretRule.IsNonIdentifying(name));

        var problems = new List<VariantProblem>();
        var normalized = VariantNormalizer.Normalize(document.RootElement, "/params", problems);

        normalized.ShouldNotBeNull();
        problems.ShouldBeEmpty();
        using var stored = JsonDocument.Parse(normalized.ParamsJson);
        stored.RootElement.EnumerateObject().Select(member => member.Name).ShouldBe(["browser"]);
    }

    /// <summary>One manifest with the same params in the matrix and in the single planned execution.</summary>
    internal static string ManifestJson(
        Guid suiteRevision,
        Guid caseRevision,
        string parameters,
        Guid? environmentRevision = null,
        bool includePlanned = true,
        bool includeMatrix = true,
        string targetEnv = "staging")
    {
        var caseText = caseRevision.ToString("D", CultureInfo.InvariantCulture);
        var environment = environmentRevision is { } id
            ? "{\"environment_revision_id\":\"" + id.ToString("D", CultureInfo.InvariantCulture) + "\"},"
            : string.Empty;
        var planned = includePlanned
            ? "{\"case_revision_id\":\"" + caseText + "\",\"variant\":{\"params\":" + parameters + "},\"target_env\":\"" + targetEnv + "\"}"
            : string.Empty;
        var variants = includeMatrix ? "{\"params\":" + parameters + "}" : string.Empty;
        var caseList = includePlanned ? "\"" + caseText + "\"" : string.Empty;
        var plannedCount = includePlanned ? 1 : 0;

        return "{\"schema_version\":1,\"suite_revision_id\":\"" + suiteRevision.ToString("D", CultureInfo.InvariantCulture) + "\","
            + environment
            + "\"planned_executions\":[" + planned + "],\"matrix\":{\"case_revision_ids\":[" + caseList
            + "],\"variants\":[" + variants + "],\"target_envs\":[\"" + targetEnv + "\"]},\"shard_plan\":{\"shards\":["
            + "{\"shard_id\":\"s0\",\"from_index\":0,\"to_index\":" + plannedCount.ToString(CultureInfo.InvariantCulture)
            + "}]},\"env_needs\":{\"target_envs\":[{\"target_env\":\"" + targetEnv + "\"}]}}";
    }

    internal static async Task<ApiAnswer> UploadAsync(HttpClient client, Action<System.Net.Http.Headers.HttpRequestHeaders> caller, Guid project, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project}/manifests");
        caller(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    internal static Dictionary<string, string> Parameters(int count, int length, char character = 'x') =>
        Enumerable.Range(0, count).ToDictionary(
            index => "k" + index.ToString("D2", CultureInfo.InvariantCulture),
            _ => new string(character, length),
            StringComparer.Ordinal);

    /// <summary>A params object whose RFC 8785-style stored text is exactly <paramref name="bytes"/> UTF-8 bytes.</summary>
    internal static string SizedParameters(int bytes, bool cjk = false)
    {
        var parameters = Parameters(cjk ? 6 : 32, 0);
        var remaining = bytes - Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(parameters));
        var cost = cjk ? 6 : 1;
        foreach (var key in parameters.Keys.ToArray())
        {
            var length = Math.Min(256, remaining / cost);
            parameters[key] = new string(cjk ? '\u754c' : 'x', length);
            remaining -= length * cost;
            if (remaining < cost && length < 256)
            {
                parameters[key] += new string('x', remaining);
                remaining = 0;
            }
        }

        var json = JsonSerializer.Serialize(parameters);
        Encoding.UTF8.GetByteCount(json).ShouldBe(bytes);
        parameters.Values.ShouldAllBe(value => value.Length <= 256);

        return json;
    }

    internal static string AstralParameters(int codePoints) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["browser"] = string.Concat(Enumerable.Repeat("\U0001f600", codePoints)) });

    private static string DuplicateMembers(int count) =>
        "{" + string.Join(',', Enumerable.Repeat("\"browser\":\"x\"", count)) + "}";

    private static string HintParameters(int count) => JsonSerializer.Serialize(
        Enumerable.Range(0, count).ToDictionary(
            index => "_hint" + index.ToString("D2", CultureInfo.InvariantCulture),
            _ => new string('\u754c', 256),
            StringComparer.Ordinal));

    private static string SecretOversizeParameters()
    {
        var parameters = Parameters(31, 256);
        parameters["mode"] = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

        return JsonSerializer.Serialize(parameters);
    }

    private static string NonIdentifyingParameters()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["browser"] = "chromium" };
        foreach (var key in new[]
                 {
                     "description", "label", "labels", "note", "notes", "comment", "comments", "title", "tags", "display_name", "displayname",
                 })
        {
            parameters[key] = new string('\u754c', 256);
        }

        return JsonSerializer.Serialize(parameters);
    }

    private static object[] Row(string name, string parameters, bool refused) => [name, parameters, refused];
}

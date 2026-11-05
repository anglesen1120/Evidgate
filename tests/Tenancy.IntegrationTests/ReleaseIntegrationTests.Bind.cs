// The explicit bind route POST /v1/releases/{id}/runs and the digest match it applies: the release's component
// digests against the run's recorded `components` map. Everything here reads the stored rows, so a response that
// claims a binding the database does not hold fails.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>One bind request body: the run named, and nothing else.</summary>
    private static string BindBody(Guid runId) => $$"""{"run_id":"{{runId:D}}"}""";

    /// <summary>One <c>POST /v1/releases/{id}/runs</c>, with the caller, key and body the case names.</summary>
    private static async Task<ApiAnswer> BindAsync(
        RunCreationTests.World world,
        Guid releaseId,
        Guid? runId = null,
        Action<HttpRequestHeaders>? caller = null,
        HttpClient? client = null,
        string? idempotencyKey = null,
        bool omitIdempotencyKey = false,
        string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/releases/{releaseId:D}/runs");
        (caller ?? world.Admin)(request.Headers);
        if (!omitIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? NewKey());
        }

        request.Content = new StringContent(
            body ?? BindBody(runId ?? Guid.CreateVersion7()),
            Encoding.UTF8,
            "application/json");

        using var response = await (client ?? world.Client).SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>One release whose component digests are the requested fill characters, in <c>state</c>.</summary>
    private static Task<Guid> ReleaseWithAsync(
        RunCreationTests.World world,
        string name,
        string? state,
        params (string Key, char Fill)[] components) =>
        CreateReleaseAsync(world, name, state, [.. components.Select(component => new Component(
            component.Key,
            Digest(component.Fill),
            world.EnvironmentRevision))]);

    /// <summary>The run's <c>components</c> map as the run-create body spells it.</summary>
    private static string Components(params (string Key, char Fill)[] components) =>
        "{" + string.Join(",", components.Select(component => $"\"{component.Key}\":\"{Digest(component.Fill)}\"")) + "}";

    /// <summary>The run-create body, optionally naming the release the run should bind to.</summary>
    private static string RunBodyWith(Guid manifestId, string componentsJson, Guid? releaseId = null)
    {
        var release = releaseId is null ? string.Empty : $",\"release_id\":\"{releaseId:D}\"";

        return $$"""{"manifest_id":"{{manifestId:D}}","components":{{componentsJson}}{{release}}}""";
    }

    /// <summary>Creates one run through the served route and returns its id, asserting the 201.</summary>
    private static async Task<Guid> RunWithAsync(
        RunCreationTests.World world,
        string componentsJson,
        Guid? releaseId = null)
    {
        var answer = await RunCreationTests.CreateRunAsync(
            world,
            RunBodyWith(world.ManifestId, componentsJson, releaseId));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer.Id("run_id");
    }

    /// <summary>The bound component keys of one (release, run) pair, in key order, read as the migration role.</summary>
    private async Task<IReadOnlyList<string>> BoundKeysOfAsync(
        RunCreationTests.World world,
        Guid releaseId,
        Guid runId) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"select component_key from public.release_run where org_id = '{world.Org:D}' "
            + $"and release_id = '{releaseId:D}' and run_id = '{runId:D}' order by component_key").ConfigureAwait(false);

    /// <summary>How many <c>release_run</c> rows the pair holds.</summary>
    private async Task<long> BindingCountAsync(
        RunCreationTests.World world,
        Guid releaseId,
        Guid runId) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}' "
            + $"and release_id = '{releaseId:D}' and run_id = '{runId:D}'").ConfigureAwait(false);

    /// <summary>The 201 body's <c>bound</c> list as component key → digest.</summary>
    private static Dictionary<string, string> BoundOf(ApiAnswer answer) =>
        answer.Body.GetProperty("bound").EnumerateArray().ToDictionary(
            item => item.GetProperty("component_key").GetString()!,
            item => item.GetProperty("digest").GetString()!,
            StringComparer.Ordinal);

    /// <summary>One <c>mismatched[]</c> entry of the bind answer.</summary>
    private sealed record Mismatch(string ComponentKey, string Reason, string Expected, string? Actual);

    /// <summary>The 201 body's <c>mismatched</c> list, in the order the answer carries it.</summary>
    private static IReadOnlyList<Mismatch> MismatchedOf(ApiAnswer answer) =>
    [
        .. answer.Body.GetProperty("mismatched").EnumerateArray().Select(item => new Mismatch(
            item.GetProperty("component_key").GetString()!,
            item.GetProperty("reason").GetString()!,
            item.GetProperty("expected_digest").GetString()!,
            item.GetProperty("actual_digest").ValueKind == JsonValueKind.Null
                ? null
                : item.GetProperty("actual_digest").GetString())),
    ];

    [Fact]
    public async Task ExplicitBind_BindsEveryMatchingComponent()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-exact", "active", ("web", 'a'), ("api", 'b'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'b')));

        var answer = await BindAsync(world, releaseId, runId);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.EnumerateObject().Select(member => member.Name)
            .ShouldBe(["release_id", "run_id", "bound", "mismatched"], ignoreOrder: true);
        BoundOf(answer).ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web"] = Digest('a'),
            ["api"] = Digest('b'),
        });
        MismatchedOf(answer).ShouldBeEmpty();

        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["api", "web"]);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' "
            + $"and action = 'release.bind' and object_type = 'release' and object_id = '{releaseId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task ExplicitBind_PartialMatch_ListsEveryMismatchWithItsReason()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(
            world, "bind-partial", "active", ("web", 'a'), ("api", 'b'), ("worker", 'c'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("api", 'd')));

        var answer = await BindAsync(world, releaseId, runId);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        BoundOf(answer).ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal) { ["web"] = Digest('a') });

        var mismatched = MismatchedOf(answer).ToDictionary(entry => entry.ComponentKey, StringComparer.Ordinal);
        mismatched.Keys.ShouldBe(["api", "worker"], ignoreOrder: true);
        mismatched["api"].ShouldBe(new Mismatch("api", "digest_mismatch", Digest('b'), Digest('d')));
        mismatched["worker"].ShouldBe(new Mismatch("worker", "component_missing", Digest('c'), null));

        // Only the matched key is stored: a mismatch is computed on read and never persisted.
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["web"]);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}' and run_id = '{runId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task ExplicitBind_IgnoresRunComponentsTheReleaseDoesNotDeclare()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-extra", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a'), ("docs", 'e'), ("mobile", 'f')));

        var answer = await BindAsync(world, releaseId, runId);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        BoundOf(answer).ShouldBe(new Dictionary<string, string>(StringComparer.Ordinal) { ["web"] = Digest('a') });
        MismatchedOf(answer).ShouldBeEmpty("a component the release does not declare is not part of the release");
        (await BoundKeysOfAsync(world, releaseId, runId)).ShouldBe(["web"]);
    }

    [Fact]
    public async Task ExplicitBind_ZeroMatch_422ReleaseComponentMismatch_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-none", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'g')));

        var answer = await BindAsync(world, releaseId, runId);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("release_component_mismatch");
        var detail = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ToArray().ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/run/components/web");
        detail.GetProperty("reason").GetString().ShouldBe("digest_mismatch");
        detail.TryGetProperty("value", out _).ShouldBeFalse(
            "the error envelope carries no free-form value, so a digest never leaks into a refusal");

        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'release.bind'")).ShouldBe(0);
    }

    [Fact]
    public async Task ExplicitBind_NewerMismatchedRun_IsNotBound()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-recency", "active", ("web", 'a'));

        // The matching run is older; the run created after it carries another build and must not be preferred.
        var older = await RunWithAsync(world, Components(("web", 'a')));
        (await BindAsync(world, releaseId, older)).Status.ShouldBe(HttpStatusCode.Created);
        var newer = await RunWithAsync(world, Components(("web", 'b')));

        (await BindAsync(world, releaseId, newer)).ErrorCode.ShouldBe("release_component_mismatch");

        (await BoundKeysOfAsync(world, releaseId, older)).ShouldBe(["web"]);
        (await BindingCountAsync(world, releaseId, newer)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_run where org_id = '{world.Org:D}' and release_id = '{releaseId:D}'"))
            .ShouldBe(1);
    }
}

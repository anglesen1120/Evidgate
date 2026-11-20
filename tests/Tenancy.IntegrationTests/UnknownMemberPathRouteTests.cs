using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// An unknown body member is echoed only when its name is safe and bounded. These requests reach the closed-member
/// scan; earlier text guards can refuse NUL or unpaired-surrogate names before that scan.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UnknownMemberPathRouteTests(
    PostgresTenancyFixture fixture,
    TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    private const string Placeholder = "/<invalid-member-name>";
    private const string AdditionalProperties = "additional_properties";

    /// <summary>A refusal that echoed a member name stays far below this; only the placeholder is sent instead.</summary>
    private const int RefusalBodyBound = 2 * 1024;

    // ---------------------------------------------------------------- create-run

    [Fact]
    public async Task CreateRun_UnknownShortName_422NamesIt_AndTheSameBodyWithoutItCreates()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var body = Member(world.ManifestId, "zzz");

        AssertRefusal(await PostAsync(world.Client, RunRoute(world), body, world.Admin), HttpStatusCode.UnprocessableEntity, "/zzz");

        var control = await PostAsync(world.Client, RunRoute(world), RunCreationTests.RunBody(world.ManifestId), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    [Fact]
    public async Task CreateRun_UnsafeAndBoundaryNames_AnswerPlaceholder()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

        foreach (var name in UnsafeNames())
        {
            var body = Member(world.ManifestId, name);
            AssertRefusal(await PostAsync(world.Client, RunRoute(world), body, world.Admin), HttpStatusCode.UnprocessableEntity, Placeholder);
        }

        var boundary = new string('a', 64);
        AssertRefusal(
            await PostAsync(world.Client, RunRoute(world), Member(world.ManifestId, boundary), world.Admin),
            HttpStatusCode.UnprocessableEntity,
            "/" + boundary);

        var control = await PostAsync(world.Client, RunRoute(world), RunCreationTests.RunBody(world.ManifestId), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    [Fact]
    public async Task CreateRun_TenThousandCharacterName_AnswersPlaceholderWithinTheBodyBound()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var name = new string('n', 10_000);

        var answer = await PostAsync(world.Client, RunRoute(world), Member(world.ManifestId, name), world.Admin);
        var text = answer.Body.GetRawText();

        AssertRefusal(answer, HttpStatusCode.UnprocessableEntity, Placeholder);
        text.Length.ShouldBeLessThan(RefusalBodyBound);
        text.ShouldNotContain(new string('n', 100));

        var control = await PostAsync(world.Client, RunRoute(world), RunCreationTests.RunBody(world.ManifestId), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    [Fact]
    public async Task CreateRun_UnknownRefMembers_EchoOnlySafeNames_AndTheSameBodyWithoutThemCreates()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        AssertRefusal(
            await PostAsync(world.Client, RunRoute(world), RefBody(world.ManifestId, "zzz"), world.Admin),
            HttpStatusCode.UnprocessableEntity,
            "/ref/zzz");

        foreach (var name in UnsafeNames())
        {
            AssertRefusal(
                await PostAsync(world.Client, RunRoute(world), RefBody(world.ManifestId, name), world.Admin),
                HttpStatusCode.UnprocessableEntity,
                Placeholder);
        }

        var boundary = new string('a', 64);
        AssertRefusal(
            await PostAsync(world.Client, RunRoute(world), RefBody(world.ManifestId, boundary), world.Admin),
            HttpStatusCode.UnprocessableEntity,
            "/ref/" + boundary);

        var longAnswer = await PostAsync(
            world.Client, RunRoute(world), RefBody(world.ManifestId, new string('n', 10_000)), world.Admin);
        AssertRefusal(longAnswer, HttpStatusCode.UnprocessableEntity, Placeholder);
        longAnswer.Body.GetRawText().Length.ShouldBeLessThan(RefusalBodyBound);

        var control = await PostAsync(world.Client, RunRoute(world), RefBody(world.ManifestId), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public async Task CancelRun_UnknownShortName_422NamesIt_AndAValidReasonIsAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, Member("zzz", ("reason", "probe")), world.Admin);

        AssertRefusal(answer, HttpStatusCode.UnprocessableEntity, "/zzz");

        var control = await CancelRunTestData.CancelRawAsync(world.Client, runId, """{"reason":"probe"}""", world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Accepted, control.ErrorMessage);
    }

    [Fact]
    public async Task CancelRun_UnsafeOrLongName_AnswersPlaceholderWithinTheBodyBound()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        foreach (var name in UnsafeNames())
        {
            var answer = await CancelRunTestData.CancelRawAsync(
                world.Client, runId, Member(name, ("reason", "probe")), world.Admin);
            AssertRefusal(answer, HttpStatusCode.UnprocessableEntity, Placeholder);
        }

        // 4 KiB is the cancel cap, so the over-long case is 3 000 characters rather than 10 000.
        var longName = new string('n', 3_000);
        var longAnswer = await CancelRunTestData.CancelRawAsync(
            world.Client, runId, Member(longName, ("reason", "probe")), world.Admin);
        var text = longAnswer.Body.GetRawText();

        AssertRefusal(longAnswer, HttpStatusCode.UnprocessableEntity, Placeholder);
        text.Length.ShouldBeLessThan(RefusalBodyBound);
        text.ShouldNotContain(new string('n', 100));

        var control = await CancelRunTestData.CancelRawAsync(world.Client, runId, """{"reason":"probe"}""", world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Accepted, control.ErrorMessage);
    }

    // ---------------------------------------------------------------- finalize

    [Fact]
    public async Task Finalize_UnknownShortName_422NamesIt_AndTheDeclaredBodyIsAccepted()
    {
        await using var world = await IngestTestWorld.StartAsync(
            fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: hosts);
        await RunStateWalk.MoveAsync(fixture, world.Database, world.RunId, "running");
        var manifestHash = await ManifestHashAsync(world);

        AssertRefusal(
            await PostAsync(world.Client, FinalizeRoute(world), FinalizeBody(manifestHash, "zzz"), NoHeaders),
            HttpStatusCode.UnprocessableEntity,
            "/zzz");

        var control = await PostAsync(world.Client, FinalizeRoute(world), FinalizeBody(manifestHash), NoHeaders);
        control.Status.ShouldBe(HttpStatusCode.Accepted, control.ErrorMessage);
    }

    [Fact]
    public async Task Finalize_UnsafeOrLongName_AnswersPlaceholderWithinTheBodyBound()
    {
        await using var world = await IngestTestWorld.StartAsync(
            fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: hosts);
        await RunStateWalk.MoveAsync(fixture, world.Database, world.RunId, "running");
        var manifestHash = await ManifestHashAsync(world);

        foreach (var name in UnsafeNames())
        {
            var body = FinalizeBody(manifestHash, name);
            var answer = await PostAsync(world.Client, FinalizeRoute(world), body, NoHeaders);
            AssertRefusal(answer, HttpStatusCode.UnprocessableEntity, Placeholder);
        }

        // 8 KiB is the finalize cap, so the over-long case is 3 000 characters rather than 10 000.
        var longName = new string('n', 3_000);
        var longAnswer = await PostAsync(
            world.Client, FinalizeRoute(world), FinalizeBody(manifestHash, longName), NoHeaders);
        var text = longAnswer.Body.GetRawText();

        AssertRefusal(longAnswer, HttpStatusCode.UnprocessableEntity, Placeholder);
        text.Length.ShouldBeLessThan(RefusalBodyBound);
        text.ShouldNotContain(new string('n', 100));

        var control = await PostAsync(world.Client, FinalizeRoute(world), FinalizeBody(manifestHash), NoHeaders);
        control.Status.ShouldBe(HttpStatusCode.Accepted, control.ErrorMessage);
    }

    // ---------------------------------------------------------------- release create

    [Fact]
    public async Task CreateRelease_UnknownShortName_422NamesIt_AndTheSameBodyWithoutItCreates()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

        AssertRefusal(
            await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world, "zzz"), world.Admin),
            HttpStatusCode.UnprocessableEntity,
            "/zzz");

        var control = await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    [Fact]
    public async Task CreateRelease_UnsafeAndBoundaryNames_AnswerPlaceholder()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);

        foreach (var name in UnsafeNames())
        {
            AssertRefusal(
                await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world, name), world.Admin),
                HttpStatusCode.UnprocessableEntity,
                Placeholder);
        }

        var boundary = new string('a', 64);
        AssertRefusal(
            await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world, boundary), world.Admin),
            HttpStatusCode.UnprocessableEntity,
            "/" + boundary);

        var control = await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    [Fact]
    public async Task CreateRelease_TenThousandCharacterName_AnswersPlaceholderWithinTheBodyBound()
    {
        await using var world = await RunCreationTests.StartAsync(fixture, cases: 1, hosts: hosts);
        var name = new string('n', 10_000);

        var answer = await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world, name), world.Admin);
        var text = answer.Body.GetRawText();

        AssertRefusal(answer, HttpStatusCode.UnprocessableEntity, Placeholder);
        text.Length.ShouldBeLessThan(RefusalBodyBound);
        text.ShouldNotContain(new string('n', 100));

        var control = await PostAsync(world.Client, ReleaseRoute(world), ReleaseBody(world), world.Admin);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A caller whose identity the host injects (the service-principal seam), so no header is added here.</summary>
    private static readonly Action<HttpRequestHeaders> NoHeaders = _ => { };

    /// <summary>Names whose echo could steer the pointer, hide a member, split the line or exceed the UTF-16 unit bound.</summary>
    private static IEnumerable<string> UnsafeNames() =>
        ["a/b", "a~b", "a\nb", "a\u0001b", "a\u200bb", "a\u2028b", "a\u2029b", new string('a', 65)];

    private static string RunRoute(RunCreationTests.World world) => $"/v1/projects/{world.Project}/runs";

    private static string ReleaseRoute(RunCreationTests.World world) => $"/v1/projects/{world.Project}/releases";

    private static string FinalizeRoute(IngestTestWorld world) => $"/v1/runs/{world.RunId:D}/finalize";

    /// <summary>A create-run body whose only content is the manifest and one member under <paramref name="name"/>.</summary>
    private static string Member(Guid manifestId, string name) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["manifest_id"] = manifestId, [name] = 1 });

    /// <summary>A body whose only member is <paramref name="name"/>, plus the named extras a route requires.</summary>
    private static string Member(string name, params (string Name, object Value)[] extra)
    {
        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (extraName, value) in extra)
        {
            members[extraName] = value;
        }

        members[name] = 1;
        return JsonSerializer.Serialize(members);
    }

    private static string RefBody(Guid manifestId, string? unknownMember = null)
    {
        var reference = new Dictionary<string, object?> { ["kind"] = "branch", ["name"] = "main" };
        if (unknownMember is not null)
        {
            reference[unknownMember] = 1;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["manifest_id"] = manifestId,
            ["ref"] = reference,
        });
    }

    private static string FinalizeBody(string manifestHash, string? unknownMember = null)
    {
        var members = new Dictionary<string, object?>
        {
            ["producer_id"] = IngestTestWorld.ProducerId,
            ["shard"] = 1,
            ["last_sequence"] = 3,
            ["count"] = 3,
            ["manifest_hash"] = manifestHash,
        };
        if (unknownMember is not null)
        {
            members[unknownMember] = 1;
        }

        return JsonSerializer.Serialize(members);
    }

    private static string ReleaseBody(RunCreationTests.World world, string? unknownMember = null)
    {
        var members = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "release-path",
            ["components"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["component_key"] = "web",
                    ["digest"] = "sha256:" + new string('a', 64),
                    ["environment_revision_id"] = world.EnvironmentRevision,
                },
            },
        };
        if (unknownMember is not null)
        {
            members[unknownMember] = 1;
        }

        return JsonSerializer.Serialize(members);
    }

    private static async Task<string> ManifestHashAsync(IngestTestWorld world) =>
        await world.ScalarAsync<string>(
            "select m.manifest_hash from public.manifest m "
            + $"join public.run r on r.org_id = m.org_id and r.manifest_id = m.id where r.id = '{world.RunId:D}'");

    private static async Task<ApiAnswer> PostAsync(
        HttpClient client,
        string route,
        string body,
        Action<HttpRequestHeaders> caller)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        caller(request.Headers);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", NewKey());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static void AssertRefusal(ApiAnswer answer, HttpStatusCode status, string path)
    {
        answer.Status.ShouldBe(status, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");

        var details = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
        details.Count.ShouldBe(1, "one refused member is one detail");
        details[0].GetProperty("path").GetString().ShouldBe(path);
        details[0].GetProperty("reason").GetString().ShouldBe(AdditionalProperties);
    }
}

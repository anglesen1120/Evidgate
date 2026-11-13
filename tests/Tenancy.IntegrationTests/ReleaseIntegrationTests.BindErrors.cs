// The bind route's own body rules and size cap: the closed body is refused with the shared `request_invalid`
// envelope, an empty body is the same refusal, and the 64 KiB cap admits exactly its own size and refuses one byte
// more with `payload_too_large`. None of these refusals writes a binding.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>The published cap of the bind route: 64 KiB, the same number the release create publishes.</summary>
    private const int BindBodyCap = 64 * 1024;

    /// <summary>A body no route here accepts: JSON, but a root that is not an object.</summary>
    private const string NonObjectBody = "[]";

    /// <summary>Asserts the shared refusal envelope: the status, the code, and that it names this route's member.</summary>
    private static void AssertBodyRefused(ApiAnswer answer, HttpStatusCode status, string code, string? detailPath)
    {
        answer.Status.ShouldBe(status, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(code);

        var error = answer.Body.GetProperty("error");
        error.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse(
            "a body the client sent wrong is not retryable as it stands");

        if (detailPath is not null)
        {
            error.GetProperty("details").EnumerateArray()
                .Select(detail => detail.GetProperty("path").GetString())
                .ShouldContain(detailPath);
        }
    }

    [Fact]
    public async Task Bind_EmptyBody_400RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-empty", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(world, releaseId, runId, body: string.Empty);

        AssertBodyRefused(answer, HttpStatusCode.BadRequest, "request_invalid", string.Empty);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Bind_BodyThatIsNotJson_400RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-notjson", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(world, releaseId, runId, body: "{\"run_id\": ");

        AssertBodyRefused(answer, HttpStatusCode.BadRequest, "request_invalid", string.Empty);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Bind_EmptyObject_422RequestInvalid_NamesTheRequiredMember()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-emptyobject", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(world, releaseId, runId, body: "{}");

        AssertBodyRefused(answer, HttpStatusCode.UnprocessableEntity, "request_invalid", "/run_id");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Bind_BodyThatIsNotAnObject_422RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-array", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(world, releaseId, runId, body: NonObjectBody);

        AssertBodyRefused(answer, HttpStatusCode.UnprocessableEntity, "request_invalid", "$");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Bind_MalformedRunId_422RequestInvalid_NamesTheMember()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-malformed", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(world, releaseId, runId, body: "{\"run_id\":\"not-a-uuid\"}");

        AssertBodyRefused(answer, HttpStatusCode.UnprocessableEntity, "request_invalid", "/run_id");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Bind_RepeatedMember_422RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-repeat", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        var body = $$"""{"run_id":"{{runId:D}}","run_id":"{{Guid.CreateVersion7():D}}"}""";

        var answer = await BindAsync(world, releaseId, runId, body: body);

        AssertBodyRefused(answer, HttpStatusCode.UnprocessableEntity, "request_invalid", null);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task Bind_UnknownMember_422RequestInvalid_NamesIt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-unknown", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        var body = $$"""{"run_id":"{{runId:D}}","release_id":"{{releaseId:D}}"}""";

        var answer = await BindAsync(world, releaseId, runId, body: body);

        AssertBodyRefused(answer, HttpStatusCode.UnprocessableEntity, "request_invalid", "/release_id");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    /// <summary>
    /// The cap is inclusive: a body of exactly 64 KiB reaches the shape rules (which refuse its padding member), and
    /// one byte more never reaches them at all.
    /// </summary>
    [Fact]
    public async Task Bind_BodyAtTheCapReachesTheShapeRules_OneByteMoreIs413()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var releaseId = await ReleaseWithAsync(world, "bind-error-cap", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var prefix = $$"""{"run_id":"{{runId:D}}","pad":" """;
        var suffix = "\"}";
        var padding = new string('x', BindBodyCap - prefix.Length - suffix.Length);
        var atCap = prefix + padding + suffix;
        atCap.Length.ShouldBe(BindBodyCap);

        var accepted = await BindAsync(world, releaseId, runId, body: atCap);
        AssertBodyRefused(accepted, HttpStatusCode.UnprocessableEntity, "request_invalid", "/pad");

        var over = await BindAsync(world, releaseId, runId, body: atCap + "y");
        AssertBodyRefused(over, HttpStatusCode.RequestEntityTooLarge, "payload_too_large", null);

        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }
}

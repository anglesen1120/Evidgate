using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The tracker connect and disconnect routes over the real host, tenant resolution and permission evaluation on a real
/// PostgreSQL, with only the provider's token endpoint stubbed: the browser callback resolves its tenant from the state,
/// refuses without consuming, stores no token on a denial, answers no token anywhere, and hides another tenant's connection.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class TrackerCallbackEndpointTests
{
    private readonly PostgresTenancyFixture _fixture;

    public TrackerCallbackEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Callback_StateNamingNoOrg_Answers401AndLeavesTheConsentUnconsumed()
    {
        await using var world = await StartAsync();
        var tenant = await NewTenantAsync(world, "tracker-noorg");
        var state = await StartConsentAsync(world, tenant);

        var refused = await CallbackAsync(world, tenant, "not-an-org-state");

        refused.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAsync(world, "tracker_oauth_state", tenant.Org, "consumed_at is null")).ShouldBe(1);
        world.Provider.Requests.ShouldBeEmpty();

        // Control: the refusal consumed nothing, so the real state still completes the connection.
        var completed = await CallbackAsync(world, tenant, state);
        completed.Status.ShouldBe(HttpStatusCode.OK);
        world.Provider.Requests.ShouldBe([TrackerEndpoints.LinearTokenUrl]);
    }

    [Fact]
    public async Task Authorize_UnconfiguredProvider_Answers409AndStoresNoConsent()
    {
        await using var unconfigured = await StartAsync(configured: false);
        var tenant = await NewTenantAsync(unconfigured, "tracker-unconf");

        var refused = await AuthorizeAsync(unconfigured, tenant);

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe(ApiErrorCode.TrackerProviderUnavailable);
        (await CountAsync(unconfigured, "tracker_oauth_state", tenant.Org)).ShouldBe(0);

        // Control: the same request on a deployment with the client configured starts one consent.
        await using var configured = await StartAsync();
        var other = await NewTenantAsync(configured, "tracker-conf");
        (await AuthorizeAsync(configured, other)).Status.ShouldBe(HttpStatusCode.OK);
        (await CountAsync(configured, "tracker_oauth_state", other.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task Callback_AccessDenied_StoresNothingAndConsumesTheState()
    {
        await using var world = await StartAsync();
        var tenant = await NewTenantAsync(world, "tracker-denied");
        var state = await StartConsentAsync(world, tenant);

        var denied = await CallbackAsync(world, tenant, state, providerError: "access_denied");

        denied.Status.ShouldBe(HttpStatusCode.BadRequest);
        denied.ErrorCode.ShouldBe(ApiErrorCode.TrackerConsentDenied);
        (await CountAsync(world, "tracker_connection", tenant.Org)).ShouldBe(0);
        (await CountAsync(world, "tracker_oauth_state", tenant.Org, "consumed_at is not null")).ShouldBe(1);
        world.Provider.Requests.ShouldBeEmpty();

        var replay = await CallbackAsync(world, tenant, state);
        replay.Status.ShouldBe(HttpStatusCode.BadRequest);
        replay.ErrorCode.ShouldBe(ApiErrorCode.TrackerConsentInvalid);
        world.Provider.Requests.ShouldBeEmpty();

        // Control: a fresh consent for the same project and tracker still connects.
        var fresh = await CallbackAsync(world, tenant, await StartConsentAsync(world, tenant));
        fresh.Status.ShouldBe(HttpStatusCode.OK);
        (await CountAsync(world, "tracker_connection", tenant.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task Callback_FullFlow_StoresTheConnectionAndLeaksNoTokenCanary()
    {
        await using var world = await StartAsync();
        var tenant = await NewTenantAsync(world, "tracker-full");
        var state = await StartConsentAsync(world, tenant);

        var answer = await CallbackAsync(world, tenant, state);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Text("provider").ShouldBe("linear");
        answer.Text("state").ShouldBe("active");
        var connection = answer.Id();
        world.Provider.Requests.ShouldBe([TrackerEndpoints.LinearTokenUrl]);

        (await CountAsync(world, "tracker_connection", tenant.Org, $"id = '{connection:D}' and state = 'active'")).ShouldBe(1);
        var stored = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select access_token_cipher || refresh_token_cipher from public.tracker_connection where id = '{connection:D}'");
        stored.ShouldHaveSingleItem().ShouldNotBeNullOrEmpty();
        foreach (var canary in world.Canaries)
        {
            stored[0].Contains(canary, StringComparison.Ordinal).ShouldBeFalse("a token is stored as plaintext");
        }

        await AssertNoCanaryAsync(world, tenant, answer.Body.GetRawText());

        var list = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{tenant.Project}/tracker-connections",
            tenant.Member);
        list.Status.ShouldBe(HttpStatusCode.OK);
        list.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())
            .ShouldBe([connection]);
        foreach (var canary in world.Canaries)
        {
            list.Body.GetRawText().Contains(canary, StringComparison.Ordinal).ShouldBeFalse("a canary is in the list");
        }
    }

    [Fact]
    public async Task Disconnect_ForeignTenantConnectionId_Answers404LikeUnknownAndLeavesVictimActive()
    {
        await using var world = await StartAsync();
        var victim = await NewTenantAsync(world, "tracker-victim");
        var other = await NewTenantAsync(world, "tracker-other");
        var connection = (await CallbackAsync(world, victim, await StartConsentAsync(world, victim))).Id();

        var unknown = await DisconnectAsync(world, other, Guid.CreateVersion7());
        var foreign = await DisconnectAsync(world, other, connection);

        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.Status.ShouldBe(unknown.Status);
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage);
        (await CountAsync(world, "tracker_connection", victim.Org, $"id = '{connection:D}' and state = 'active' and access_token_cipher is not null"))
            .ShouldBe(1);

        // Control: the owner's own disconnect succeeds and clears both tokens.
        var own = await DisconnectAsync(world, victim, connection);
        own.Status.ShouldBeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
        (await CountAsync(
            world,
            "tracker_connection",
            victim.Org,
            $"id = '{connection:D}' and state = 'disconnected' and access_token_cipher is null and refresh_token_cipher is null"))
            .ShouldBe(1);
    }
}

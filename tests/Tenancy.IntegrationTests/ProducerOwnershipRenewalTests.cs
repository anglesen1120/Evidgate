#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.IngestEventBatchTests;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Producer slot ownership through the real credential path: an ingest token is exchanged for a bearer, the bearer is
/// sent to the events and finalize routes, and the slot is owned by the stable id of the ingest token that bound it.
/// A bearer that expired is refused, and exchanging the same ingest token again (a new <c>jti</c>, the same token id)
/// keeps the slot; another ingest token, another project's token and a bearer without a token id cannot take it. Each
/// refusal sits beside the positive control that the owner still succeeds.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ProducerOwnershipRenewalTests : IClassFixture<TenancyHostPool>
{
    private const string Producer = IngestTestWorld.ProducerId;

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public ProducerOwnershipRenewalTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    private static string NewTokenId() => Guid.CreateVersion7().ToString("D");

    /// <summary>
    /// A running run with one producer slot, plus a CI host over the same database that authenticates bearers against
    /// a clock the test moves. The world's own principal host is never used: every request here carries a bearer.
    /// </summary>
    private sealed class Scene(IngestTestWorld world, TenancyApiFactory origin, WebApplicationFactory<Program> host, FakeTimeProvider clock)
        : IAsyncDisposable
    {
        internal IngestTestWorld World { get; } = world;

        internal WebApplicationFactory<Program> Host { get; } = host;

        internal FakeTimeProvider Clock { get; } = clock;

        internal HttpClient Anonymous { get; } = host.CreateClient();

        internal Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: World.User, orgHeader: World.Org);

        public async ValueTask DisposeAsync()
        {
            Anonymous.Dispose();
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                try
                {
                    await origin.DisposeAsync();
                }
                finally
                {
                    await World.DisposeAsync();
                }
            }
        }
    }

    private async Task<Scene> StartSceneAsync()
    {
        var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(NewTokenId()), hosts: _hosts);
        TenancyApiFactory? origin = null;

        try
        {
            await world.SetRunStateAsync("running");
            var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
            origin = TenancyApiFactory.Ci(_fixture.AppConnectionString(world.Database), IngestBearer.ShortLived);
            var host = origin.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }));

            return new Scene(world, origin, host, clock);
        }
        catch
        {
            if (origin is not null)
            {
                await origin.DisposeAsync();
            }

            await world.DisposeAsync();
            throw;
        }
    }

    private static Task<(string Secret, Guid TokenId)> NewTokenAsync(Scene scene, string name, Guid? project = null) =>
        IngestBearer.CreateTokenAsync(scene.Anonymous, scene.Admin, project ?? scene.World.Project, name);

    private static Task<HttpClient> BearerAsync(Scene scene, string secret) =>
        IngestBearer.BearerClientAsync(scene.Host, scene.Anonymous, secret);

    private static Task<string> OwnerAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            $"select bound_token_jti from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{Producer}'");

    private static Task<bool> ClosedAsync(IngestTestWorld world) =>
        world.ScalarAsync<bool>(
            $"select closed_at is not null from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{Producer}'");

    private static Task<long> EventCountAsync(IngestTestWorld world) =>
        world.CountAsync("event", $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'");

    private static async Task<ApiAnswer> FinalizeAsync(IngestTestWorld world, HttpClient client)
    {
        var manifestHash = await world.ScalarAsync<string>(
            "select m.manifest_hash from public.manifest m "
            + $"join public.run r on r.org_id = m.org_id and r.manifest_id = m.id where r.id = '{world.RunId:D}'");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{world.RunId:D}/finalize");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", NewKey());
        request.Content = JsonContent.Create(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["producer_id"] = Producer,
            ["shard"] = 1,
            ["last_sequence"] = 1,
            ["count"] = 1,
            ["manifest_hash"] = manifestHash,
        });

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static object OneEvent(IngestTestWorld world) =>
        Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1));

    private static object SecondEvent(IngestTestWorld world) =>
        Batch(Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2));

    /// <summary>A correctly signed <c>ci_producer</c> bearer of the scene's project, with or without the token id claim.</summary>
    private static string Mint(Scene scene, Guid? ingestTokenId)
    {
        var world = scene.World;
        var now = scene.Clock.GetUtcNow();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = $"ci:{world.Project:D}",
            ["jti"] = Guid.CreateVersion7().ToString("D"),
            ["token_use"] = "ci_producer",
            ["org_id"] = world.Org.ToString("D"),
            ["project_id"] = world.Project.ToString("D"),
            ["perm"] = new[] { Evidgate.BuildingBlocks.SharedKernel.Authorization.Permissions.Event.Write },
        };
        if (ingestTokenId is { } id)
        {
            claims["ingest_token_id"] = id.ToString("D");
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = CiTestKeys.Issuer,
            Audience = CiTestKeys.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(1).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(TenancyApiFactory.Keys.SigningKey), SecurityAlgorithms.HmacSha256),
        });
    }

    [Fact]
    public async Task Ownership_BearerExpiresAndTheSameIngestTokenIsExchangedAgain_StillFinalizesTheSlotItBound()
    {
        await using var scene = await StartSceneAsync();
        var world = scene.World;
        var (secret, tokenId) = await NewTokenAsync(scene, "alpha");

        var first = await IngestBearer.ExchangeAsync(scene.Anonymous, secret);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        var firstBearer = first.Text("token")!;
        using var producer = IngestBearer.WithBearer(scene.Host, firstBearer);

        // The first batch binds the slot to the ingest token id, never to the bearer's jti.
        var bound = await PostEventsAsync(world, OneEvent(world), client: producer);
        bound.Status.ShouldBe(HttpStatusCode.OK, bound.ErrorMessage);
        (await OwnerAsync(world)).ShouldBe(tokenId.ToString("D"));

        // The bearer lives one minute, so two minutes later it is refused on both routes and changes nothing.
        scene.Clock.Advance(IngestBearer.PastLifetime);
        (await PostEventsAsync(world, SecondEvent(world), client: producer)).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await FinalizeAsync(world, producer)).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await ClosedAsync(world)).ShouldBeFalse();
        (await EventCountAsync(world)).ShouldBe(1);

        // Renewal is another exchange of the same ingest token: a new jti, the same ingest_token_id.
        var renewed = await IngestBearer.ExchangeAsync(scene.Anonymous, secret);
        renewed.Status.ShouldBe(HttpStatusCode.OK, renewed.ErrorMessage);
        var before = new JsonWebToken(firstBearer);
        var after = new JsonWebToken(renewed.Text("token")!);
        after.Id.ShouldNotBeNullOrEmpty();
        after.Id.ShouldNotBe(before.Id);
        before.GetPayloadValue<string>("ingest_token_id").ShouldBe(tokenId.ToString("D"));
        after.GetPayloadValue<string>("ingest_token_id").ShouldBe(tokenId.ToString("D"));

        // A second ingest token of the same project, with a valid bearer of its own, cannot close the bound slot.
        var (otherSecret, _) = await NewTokenAsync(scene, "bravo");
        using var other = await BearerAsync(scene, otherSecret);
        var taken = await FinalizeAsync(world, other);
        taken.Status.ShouldBe(HttpStatusCode.Forbidden);
        taken.ErrorCode.ShouldBe("producer_slot_taken");
        (await ClosedAsync(world)).ShouldBeFalse();

        // The renewed bearer is the owner and finalizes the slot the first bearer bound.
        using var renewedProducer = IngestBearer.WithBearer(scene.Host, renewed.Text("token")!);
        var finalized = await FinalizeAsync(world, renewedProducer);
        finalized.Status.ShouldBe(HttpStatusCode.Accepted, finalized.ErrorMessage);
        (await ClosedAsync(world)).ShouldBeTrue();
        (await OwnerAsync(world)).ShouldBe(tokenId.ToString("D"));
    }

    [Fact]
    public async Task Ownership_OtherIngestTokenOfTheSameProject_CannotPostOrFinalizeTheBoundSlot()
    {
        await using var scene = await StartSceneAsync();
        var world = scene.World;
        var (ownerSecret, ownerId) = await NewTokenAsync(scene, "alpha");
        var (otherSecret, _) = await NewTokenAsync(scene, "bravo");
        using var owner = await BearerAsync(scene, ownerSecret);
        using var other = await BearerAsync(scene, otherSecret);
        (await PostEventsAsync(world, OneEvent(world), client: owner)).Status.ShouldBe(HttpStatusCode.OK);

        var posted = await PostEventsAsync(world, SecondEvent(world), client: other);
        var finalized = await FinalizeAsync(world, other);

        posted.Status.ShouldBe(HttpStatusCode.Forbidden);
        posted.ErrorCode.ShouldBe("producer_slot_taken");
        finalized.Status.ShouldBe(HttpStatusCode.Forbidden);
        finalized.ErrorCode.ShouldBe("producer_slot_taken");
        (await OwnerAsync(world)).ShouldBe(ownerId.ToString("D"));
        (await ClosedAsync(world)).ShouldBeFalse();
        (await EventCountAsync(world)).ShouldBe(1);

        // Positive control: the same finalize request from the owner is accepted.
        var accepted = await FinalizeAsync(world, owner);
        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
    }

    [Fact]
    public async Task Ownership_TokenScopedToAnotherProject_CannotBindOrCloseTheSlot()
    {
        await using var scene = await StartSceneAsync();
        var world = scene.World;
        using var setup = world.Api.CreateClient();
        var otherProject = await CreateProjectAsync(setup, world.User, world.Org, "other");
        var (foreignSecret, _) = await NewTokenAsync(scene, "foreign", otherProject);
        var (ownSecret, ownId) = await NewTokenAsync(scene, "alpha");
        using var foreign = await BearerAsync(scene, foreignSecret);

        var posted = await PostEventsAsync(world, OneEvent(world), client: foreign);
        var finalized = await FinalizeAsync(world, foreign);

        posted.Status.ShouldBe(HttpStatusCode.Forbidden);
        posted.ErrorCode.ShouldBe("project_scope_mismatch");
        finalized.Status.ShouldBe(HttpStatusCode.Forbidden);
        finalized.ErrorCode.ShouldBe("project_scope_mismatch");
        (await OwnerAsync(world)).ShouldBeNull("the foreign project's token never bound the slot");
        (await ClosedAsync(world)).ShouldBeFalse();
        (await EventCountAsync(world)).ShouldBe(0);

        // Positive control: a token of the run's own project binds the same slot with the same request.
        using var own = await BearerAsync(scene, ownSecret);
        (await PostEventsAsync(world, OneEvent(world), client: own)).Status.ShouldBe(HttpStatusCode.OK);
        (await OwnerAsync(world)).ShouldBe(ownId.ToString("D"));
    }

    [Fact]
    public async Task Ownership_SignedBearerWithoutAnIngestTokenId_IsRefusedAndBindsNothing()
    {
        await using var scene = await StartSceneAsync();
        var world = scene.World;
        using var anonymous = IngestBearer.WithBearer(scene.Host, Mint(scene, ingestTokenId: null));

        var posted = await PostEventsAsync(world, OneEvent(world), client: anonymous);
        var finalized = await FinalizeAsync(world, anonymous);

        posted.Status.ShouldBe(HttpStatusCode.Unauthorized);
        finalized.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await OwnerAsync(world)).ShouldBeNull();
        (await ClosedAsync(world)).ShouldBeFalse();

        // Positive control: the same signed bearer with the claim binds the slot to that id.
        var tokenId = Guid.CreateVersion7();
        using var claimed = IngestBearer.WithBearer(scene.Host, Mint(scene, tokenId));
        (await PostEventsAsync(world, OneEvent(world), client: claimed)).Status.ShouldBe(HttpStatusCode.OK);
        (await OwnerAsync(world)).ShouldBe(tokenId.ToString("D"));
    }
}

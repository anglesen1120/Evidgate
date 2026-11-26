#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.IngestEventBatchTests;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.11.1 slot ownership through the real events and finalize routes: a producer slot is owned by the stable id of
/// the ingest token that bound it, so a renewed token (new <c>jti</c>, same token id) keeps the slot while another
/// ingest token, another project's token and a caller without a token id cannot take it. Each refusal is asserted
/// beside the positive control that the owner still succeeds, so a denial is never an artefact of a broken request.
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

    /// <summary>A host that carries <paramref name="principal"/>: a freshly exchanged token of that ingest token.</summary>
    private TenancyApiFactory HostFor(IngestTestWorld world, RequestPrincipal principal) =>
        _hosts.WithPrincipal(world.Database, world.Org, world.Project, principal);

    private static Task<string> OwnerAsync(IngestTestWorld world, Guid? runId = null) =>
        world.ScalarAsync<string>(
            $"select bound_token_jti from public.producer_slot where run_id = '{runId ?? world.RunId:D}' and producer_id = '{Producer}'");

    private static Task<bool> ClosedAsync(IngestTestWorld world) =>
        world.ScalarAsync<bool>(
            $"select closed_at is not null from public.producer_slot where run_id = '{world.RunId:D}' and producer_id = '{Producer}'");

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

    [Fact]
    public async Task Ownership_RenewedTokenSameTokenId_KeepsTheSlotThroughEventsAndFinalize()
    {
        var tokenId = NewTokenId();
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(tokenId), hosts: _hosts);
        await world.SetRunStateAsync("running");

        var first = await PostEventsAsync(world, OneEvent(world));
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        (await OwnerAsync(world)).ShouldBe(tokenId, "the slot is owned by the token id, never by a minted jti");

        // Renewal is another exchange of the same ingest token: a different jti, the same token id.
        await using var renewed = HostFor(world, IngestTestWorld.Writer(tokenId, world.Project));
        using var renewedClient = renewed.CreateClient();
        var answer = await FinalizeAsync(world, renewedClient);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await ClosedAsync(world)).ShouldBeTrue();
        (await OwnerAsync(world)).ShouldBe(tokenId);
    }

    [Fact]
    public async Task Ownership_OtherIngestTokenOfTheSameProject_CannotPostOrFinalizeTheBoundSlot()
    {
        var owner = NewTokenId();
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(owner), hosts: _hosts);
        await world.SetRunStateAsync("running");
        (await PostEventsAsync(world, OneEvent(world))).Status.ShouldBe(HttpStatusCode.OK);

        await using var other = HostFor(world, IngestTestWorld.Writer(NewTokenId(), world.Project));
        using var otherClient = other.CreateClient();
        var posted = await PostEventsAsync(
            world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2)), client: otherClient);
        var finalized = await FinalizeAsync(world, otherClient);

        posted.Status.ShouldBe(HttpStatusCode.Forbidden);
        posted.ErrorCode.ShouldBe("producer_slot_taken");
        finalized.Status.ShouldBe(HttpStatusCode.Forbidden);
        finalized.ErrorCode.ShouldBe("producer_slot_taken");
        (await OwnerAsync(world)).ShouldBe(owner);
        (await ClosedAsync(world)).ShouldBeFalse();
        (await world.CountAsync("event", $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'")).ShouldBe(1);

        // Positive control: the same request shape from the owner is accepted.
        var accepted = await FinalizeAsync(world, world.Client);
        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
    }

    [Fact]
    public async Task Ownership_FirstTokenToBind_KeepsTheSlotFromTheNextToken()
    {
        var first = NewTokenId();
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(NewTokenId()), hosts: _hosts);
        await world.SetRunStateAsync("running");

        // The first token to present the slot owns it, so identical declared metadata gives the second token nothing.
        await using var firstHost = HostFor(world, IngestTestWorld.Writer(first, world.Project));
        using var firstClient = firstHost.CreateClient();
        (await PostEventsAsync(world, OneEvent(world), client: firstClient)).Status.ShouldBe(HttpStatusCode.OK);

        var late = await FinalizeAsync(world, world.Client);

        late.Status.ShouldBe(HttpStatusCode.Forbidden);
        late.ErrorCode.ShouldBe("producer_slot_taken");
        (await OwnerAsync(world)).ShouldBe(first);
        (await FinalizeAsync(world, firstClient)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Ownership_TokenScopedToAnotherProject_CannotBindOrCloseTheSlot()
    {
        var tokenId = NewTokenId();
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(tokenId), hosts: _hosts);
        using var setup = world.Api.CreateClient();
        var otherProject = await CreateProjectAsync(setup, world.User, world.Org, "other");
        var otherRun = await world.SeedRunInProjectAsync(otherProject);
        await IngestTestWorld.SeedSlotsAsync(_fixture, world.Database, world.Org, otherRun.RunId, 1, boundOwnerId: null);

        var denied = await PostEventsAsync(
            world,
            Batch(Event(Guid.CreateVersion7(), otherRun.Executions[0].ExecutionKey, sequence: 1)),
            runId: otherRun.RunId);

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("project_scope_mismatch");
        (await OwnerAsync(world, otherRun.RunId)).ShouldBeNull("the foreign project's slot was never bound");

        // Positive control: the same token binds a slot of its own project.
        await world.SetRunStateAsync("running");
        (await PostEventsAsync(world, OneEvent(world))).Status.ShouldBe(HttpStatusCode.OK);
        (await OwnerAsync(world)).ShouldBe(tokenId);
    }

    [Fact]
    public async Task Ownership_CallerWithoutATokenId_FailsClosedAndBindsNothing()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(NewTokenId()), hosts: _hosts);
        await world.SetRunStateAsync("running");

        await using var anonymous = HostFor(
            world, RequestPrincipal.Service(world.Project, [Evidgate.BuildingBlocks.SharedKernel.Authorization.Permissions.Event.Write], credentialId: null));
        using var anonymousClient = anonymous.CreateClient();
        var posted = await PostEventsAsync(world, OneEvent(world), client: anonymousClient);
        var finalized = await FinalizeAsync(world, anonymousClient);

        posted.Status.ShouldBe(HttpStatusCode.Unauthorized);
        finalized.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await OwnerAsync(world)).ShouldBeNull();
        (await ClosedAsync(world)).ShouldBeFalse();

        // Positive control: a caller that has a token id binds the same slot with the same request.
        (await PostEventsAsync(world, OneEvent(world))).Status.ShouldBe(HttpStatusCode.OK);
    }
}

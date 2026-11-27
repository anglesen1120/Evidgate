using System.Security.Cryptography;
using Evidgate.Modules.Authoring.Trackers.Application;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.TrackerTestSupport;

namespace Tenancy.IntegrationTests;

/// <summary>The tracker connection store on the real database: consent single use, tenant scoping, stored ciphertext, disconnect.</summary>
[Trait("Category", "Docker")]
public sealed class TrackerConnectionStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly string[] GrantedScopes = ["read:jira-work", "offline_access"];

    private readonly PostgresTenancyFixture _fixture;

    public TrackerConnectionStoreTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Host_ResolvesTheTrackerStore()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        world.Api.Services.GetRequiredService<ITrackerConnectionStore>().ShouldBeOfType<TrackerConnectionStore>();
    }

    [Fact]
    public async Task Consent_Taken_ReturnsTheSavedConsentOnceAndThenReplayed()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var hash = NewHash();
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, hash, Consent(world), token));

        var first = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));
        var second = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));

        var taken = first.ShouldBeOfType<ConsentLookup.Taken>().Consent;
        taken.OrgId.ShouldBe(world.Org);
        taken.ProjectId.ShouldBe(world.Project);
        taken.UserId.ShouldBe(world.User);
        taken.Provider.ShouldBe(TrackerProvider.JiraCloud);
        taken.RedirectUri.ShouldBe(Redirect);
        taken.PkceVerifier.ShouldBe("pkce-verifier-canary-0123456789");
        second.ShouldBeOfType<ConsentLookup.Replayed>();
    }

    [Fact]
    public async Task Consent_TakenByAnotherUser_IsWrongUserAndStaysTakeableByTheRightOne()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var hash = NewHash();
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, hash, Consent(world), token));

        var stranger = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, Guid.CreateVersion7(), token));
        var owner = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));

        stranger.ShouldBeOfType<ConsentLookup.WrongUser>();
        owner.ShouldBeOfType<ConsentLookup.Taken>();
    }

    [Fact]
    public async Task Consent_AtOrAfterTheTtl_IsExpired_AndJustBeforeItIsTaken()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var onTime = NewHash();
        var atTtl = NewHash();
        var late = NewHash();
        foreach (var hash in new[] { onTime, atTtl, late })
        {
            await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, hash, Consent(world), token));
        }

        clock.Advance(Ttl - TimeSpan.FromSeconds(1));
        var before = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(onTime, world.User, token));
        clock.Advance(TimeSpan.FromSeconds(1));
        var boundary = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(atTtl, world.User, token));
        clock.Advance(TimeSpan.FromMinutes(5));
        var after = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(late, world.User, token));

        before.ShouldBeOfType<ConsentLookup.Taken>();
        boundary.ShouldBeOfType<ConsentLookup.Expired>();
        after.ShouldBeOfType<ConsentLookup.Expired>();
    }

    [Fact]
    public async Task Consent_UnknownDigest_IsUnknown_ButASavedOneIsNot()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var hash = NewHash();
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, hash, Consent(world), token));

        var unknown = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(NewHash(), world.User, token));
        var known = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));

        unknown.ShouldBeOfType<ConsentLookup.Unknown>();
        known.ShouldBeOfType<ConsentLookup.Taken>();
    }

    [Fact]
    public async Task Consent_OfAnotherTenant_IsUnknown_AndStaysTakeableByItsOwner()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var hash = NewHash();
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, hash, Consent(world), token));

        // The other tenant even names the owner's user: row level security alone hides the digest.
        var foreign = await other.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));
        var owner = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));

        foreign.ShouldBeOfType<ConsentLookup.Unknown>();
        owner.ShouldBeOfType<ConsentLookup.Taken>();
    }

    [Fact]
    public async Task Consent_SavedForAnotherTenant_IsRefusedBeforeAnyWrite()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var hash = NewHash();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            world.WithStoreAsync(Clock(), (store, token) => SaveAsync(store, hash, Consent(other), token)));

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.tracker_oauth_state where org_id in ('{world.Org:D}', '{other.Org:D}')"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Consent_Stored_HoldsTheDigestAndACipherOfTheVerifierNeverItsPlaintext()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var hash = NewHash();
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, hash, Consent(world), token));

        var row = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select pkce_verifier_cipher from public.tracker_oauth_state where org_id = '{world.Org:D}' and state_hash = decode('{Convert.ToHexString(hash)}', 'hex')");

        row.ShouldNotBeNullOrEmpty();
        row.ShouldNotContain("pkce-verifier-canary");
        Protector(world).UnprotectPkceVerifier(row).ShouldBe("pkce-verifier-canary-0123456789");
    }

    [Fact]
    public async Task Create_StoresCiphertextOnly_AndTheViewCarriesNoSecret()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        var view = await world.CreatedAsync(Clock());

        view.State.ShouldBe(TrackerConnectionState.Active);
        view.Provider.ShouldBe(TrackerProvider.JiraCloud);
        view.ProjectId.ShouldBe(world.Project);
        view.CreateIssueEnabled.ShouldBeFalse();
        view.GrantedScopes.ShouldBe(GrantedScopes, ignoreOrder: true);
        var access = await Column(world, view.Id, "access_token_cipher");
        var refresh = await Column(world, view.Id, "refresh_token_cipher");
        access.ShouldNotContain("access-canary");
        refresh.ShouldNotContain("refresh-canary");
        Protector(world).UnprotectToken(TrackerProvider.JiraCloud, access).ShouldBe("access-canary-token-1");
        Protector(world).UnprotectToken(TrackerProvider.JiraCloud, refresh).ShouldBe("refresh-canary-token-1");
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select created_by_membership_id from public.tracker_connection where id = '{view.Id:D}'"))
            .ShouldBe(await world.MembershipAsync());
    }

    [Fact]
    public async Task Create_ForTheLiveConnection_RefreshesItInPlaceAndKeepsAnUnreplacedRefreshToken()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var first = await world.CreatedAsync(clock);
        await _fixture.ExecuteAsync(world.Database, $"update public.tracker_connection set state = 'needs_reauth' where id = '{first.Id:D}'");

        var second = await world.CreatedAsync(clock, NewConnection(world, access: "access-canary-token-2", refresh: null));

        second.Id.ShouldBe(first.Id);
        second.State.ShouldBe(TrackerConnectionState.Active);
        var protector = Protector(world);
        protector.UnprotectToken(TrackerProvider.JiraCloud, await Column(world, first.Id, "access_token_cipher")).ShouldBe("access-canary-token-2");
        protector.UnprotectToken(TrackerProvider.JiraCloud, await Column(world, first.Id, "refresh_token_cipher")).ShouldBe("refresh-canary-token-1");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.tracker_connection where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Create_AfterADisconnect_StartsAFreshConnection()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var first = await world.CreatedAsync(clock);
        (await world.WithStoreAsync(clock, (store, token) => store.DisconnectAsync(world.Project, first.Id, token))).ShouldBeTrue();

        var second = await world.CreatedAsync(clock);

        second.Id.ShouldNotBe(first.Id);
        second.State.ShouldBe(TrackerConnectionState.Active);
    }

    [Fact]
    public void Protector_CipherOfOneProvider_DoesNotOpenForAnotherOrAsAPkceVerifier()
    {
        var protector = new TrackerTokenProtector(new EphemeralDataProtectionProvider());

        var jira = protector.ProtectToken(TrackerProvider.JiraCloud, "secret-value");
        var pkce = protector.ProtectPkceVerifier("secret-value");

        protector.UnprotectToken(TrackerProvider.JiraCloud, jira).ShouldBe("secret-value");
        protector.UnprotectPkceVerifier(pkce).ShouldBe("secret-value");
        Should.Throw<CryptographicException>(() => protector.UnprotectToken(TrackerProvider.Linear, jira));
        Should.Throw<CryptographicException>(() => protector.UnprotectToken(TrackerProvider.JiraCloud, pkce));
        Should.Throw<CryptographicException>(() => protector.UnprotectPkceVerifier(jira));
    }

    [Fact]
    public async Task Disconnect_ClearsBothCiphersAndSpendsOpenConsentsOfThatProviderOnly()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var view = await world.CreatedAsync(clock);
        var sameProvider = NewHash();
        var otherProvider = NewHash();
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, sameProvider, Consent(world), token));
        await world.WithStoreAsync(clock, (store, token) => SaveAsync(store, otherProvider, Consent(world, provider: TrackerProvider.Linear), token));

        (await world.WithStoreAsync(clock, (store, token) => store.DisconnectAsync(world.Project, view.Id, token))).ShouldBeTrue();
        (await world.WithStoreAsync(clock, (store, token) => store.DisconnectAsync(world.Project, view.Id, token))).ShouldBeFalse();

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.tracker_connection where id = '{view.Id:D}'")).ShouldBe("disconnected");
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select access_token_cipher is null and refresh_token_cipher is null and disconnected_at is not null from public.tracker_connection where id = '{view.Id:D}'"))
            .ShouldBeTrue();
        (await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(sameProvider, world.User, token)))
            .ShouldBeOfType<ConsentLookup.Replayed>();
        (await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(otherProvider, world.User, token)))
            .ShouldBeOfType<ConsentLookup.Taken>();
    }

    [Fact]
    public async Task Disconnect_OfAnotherTenantOrProject_ReturnsFalseAndLeavesTheConnectionActive()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var view = await world.CreatedAsync(clock);

        (await other.WithStoreAsync(clock, (store, token) => store.DisconnectAsync(other.Project, view.Id, token))).ShouldBeFalse();
        (await world.WithStoreAsync(clock, (store, token) => store.DisconnectAsync(world.OtherProject, view.Id, token), world.OtherProject)).ShouldBeFalse();
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.tracker_connection where id = '{view.Id:D}'")).ShouldBe("active");

        (await world.WithStoreAsync(clock, (store, token) => store.DisconnectAsync(world.Project, view.Id, token))).ShouldBeTrue();
    }

    [Fact]
    public async Task List_ReturnsTheProjectsConnectionsOnly_AndNothingAcrossTenants()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var clock = Clock();
        var jira = await world.CreatedAsync(clock);
        var linear = await world.CreatedAsync(clock, NewConnection(world, TrackerProvider.Linear));
        await world.CreatedAsync(clock, NewConnection(world, project: world.OtherProject));

        var listed = await world.WithStoreAsync(clock, (store, token) => store.ListAsync(world.Project, token));
        var foreign = await other.WithStoreAsync(clock, (store, token) => store.ListAsync(world.Project, token));

        listed.Select(view => view.Id).ShouldBe(new[] { jira.Id, linear.Id }, ignoreOrder: true);
        listed.ShouldAllBe(view => view.ProjectId == world.Project);
        foreign.ShouldBeEmpty();
    }

    [Fact]
    public void ListSql_NamesNoCipherColumn_WhileTheWriteStatementsDo()
    {
        TrackerConnectionSql.List.ShouldNotContain("cipher");
        TrackerConnectionSql.ConsentState.ShouldNotContain("cipher");
        TrackerConnectionSql.Upsert.ShouldContain("access_token_cipher");
        TrackerConnectionSql.TakeConsent.ShouldContain("pkce_verifier_cipher");
    }

    private Task<string> Column(RequirementTestWorld world, Guid id, string column) =>
        _fixture.ScalarAsync<string>(world.Database, $"select {column} from public.tracker_connection where id = '{id:D}'");

    private static async Task<bool> SaveAsync(ITrackerConnectionStore store, byte[] hash, PendingConsent consent, CancellationToken token)
    {
        await store.SaveConsentAsync(hash, consent, Ttl, token);
        return true;
    }
}

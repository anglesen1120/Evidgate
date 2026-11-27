using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Trackers.Application;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Drives the tracker connection store of the host's own tenant seam and data-protection ring inside one writable
/// scope of a requirement world, with a fake clock so expiry needs no waiting.
/// </summary>
internal static class TrackerTestSupport
{
    internal const string Redirect = "https://app.example.test/tracker-connections/jira_cloud/callback";

    /// <summary>A fresh opaque digest, as the consent endpoint would store for a generated state value.</summary>
    internal static byte[] NewHash() => SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));

    internal static FakeTimeProvider Clock() => new(TimeProvider.System.GetUtcNow());

    /// <summary>The protector the store uses, built over the host's ring so a test can unprotect what the store wrote.</summary>
    internal static TrackerTokenProtector Protector(RequirementTestWorld world) =>
        new(world.Api.Services.GetRequiredService<IDataProtectionProvider>());

    /// <summary>
    /// Runs <paramref name="call"/> on a store bound to <paramref name="clock"/> in one writable scope of
    /// <paramref name="project"/> (default: the world's), committing afterwards unless <paramref name="commit"/> is false.
    /// </summary>
    internal static async Task<T> WithStoreAsync<T>(
        this RequirementTestWorld world,
        FakeTimeProvider clock,
        Func<ITrackerConnectionStore, CancellationToken, Task<T>> call,
        Guid? project = null,
        bool commit = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, project ?? world.Project, false, cancellationToken);

        var store = new TrackerConnectionStore(
            provider.GetRequiredService<ITenantDb>(),
            provider.GetRequiredService<ITenantScopeAccessor>(),
            new TrackerTokenProtector(provider.GetRequiredService<IDataProtectionProvider>()),
            clock);

        var result = await call(store, cancellationToken);
        if (commit)
        {
            await scope.CommitAsync(cancellationToken);
        }

        return result;
    }

    internal static PendingConsent Consent(
        RequirementTestWorld world,
        Guid? user = null,
        TrackerProvider provider = TrackerProvider.JiraCloud,
        Guid? project = null,
        string? verifier = "pkce-verifier-canary-0123456789") =>
        new(world.Org, project ?? world.Project, user ?? world.User, provider, Redirect, verifier);

    internal static NewTrackerConnection NewConnection(
        RequirementTestWorld world,
        TrackerProvider provider = TrackerProvider.JiraCloud,
        string access = "access-canary-token-1",
        string? refresh = "refresh-canary-token-1",
        Guid? project = null) =>
        new(project ?? world.Project, provider, world.User, access, refresh, null, ["read:jira-work", "offline_access"]);

    /// <summary>Creates a connection through the store and commits it.</summary>
    internal static async Task<TrackerConnectionView> CreatedAsync(
        this RequirementTestWorld world,
        FakeTimeProvider clock,
        NewTrackerConnection? connection = null)
    {
        var prepared = await world.PreparedConnectionAsync(clock, connection);
        var view = await world.WithStoreAsync(clock, (store, token) => store.CreateAsync(prepared, token));
        return view.ShouldNotBeNull();
    }

    internal static async Task<NewTrackerConnection> PreparedConnectionAsync(
        this RequirementTestWorld world,
        FakeTimeProvider clock,
        NewTrackerConnection? connection = null)
    {
        var value = connection ?? NewConnection(world);
        var hash = NewHash();
        var pending = Consent(world, provider: value.Provider, project: value.ProjectId);
        (await world.WithStoreAsync(clock, (store, token) => store.SaveConsentAsync(hash, pending, TimeSpan.FromMinutes(10), token)))
            .ShouldBeTrue();
        var taken = await world.WithStoreAsync(clock, (store, token) => store.TakeConsentAsync(hash, world.User, token));
        return value with
        {
            MembershipId = await world.MembershipAsync(),
            ConsentId = taken.ShouldBeOfType<ConsentLookup.Taken>().Consent.ConsentId,
        };
    }
}

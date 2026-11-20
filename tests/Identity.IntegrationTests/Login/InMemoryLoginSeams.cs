using System.Collections.Concurrent;
using System.Xml.Linq;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// For hosts that keep the login key ring and replay set in the test process: both answer with the same
/// first-use / live-until-expiry contract the PostgreSQL stores have (proved against the real server in
/// <c>ReplayCacheTests</c> and the Hosts shared-ring tests), and replay expiry follows the clock a case moves.
/// <see cref="ApplyReplayStoreOutage"/> instead reproduces only the replay store's documented failure.
/// </summary>
internal static class InMemoryLoginSeams
{
    /// <summary>Composes the in-memory seams.</summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="clock">The clock the in-memory replay set judges its own records by.</param>
    internal static void Apply(IServiceCollection services, TimeProvider clock)
    {
        services.RemoveAll<ILoginReplayCache>();
        services.AddSingleton<ILoginReplayCache>(new InMemoryReplay(clock));

        services.Configure<KeyManagementOptions>(keys => keys.XmlRepository = new InMemoryKeys());
    }

    /// <summary>
    /// The replay seam of a case whose subject is the store's own outage: every consume throws the
    /// <see cref="NpgsqlException"/> <c>LoginReplayCache</c> throws when the database cannot answer, while the
    /// rest of the host (the rate limiter included) keeps its real database.
    /// </summary>
    /// <param name="services">The host's collection.</param>
    internal static void ApplyReplayStoreOutage(IServiceCollection services)
    {
        services.RemoveAll<ILoginReplayCache>();
        services.AddSingleton<ILoginReplayCache>(new FailingReplay());
        services.Configure<KeyManagementOptions>(keys => keys.XmlRepository = new InMemoryKeys());
    }

    /// <summary>
    /// The identity-store seam of a case whose subject is the completion's own database fault: the upsert throws
    /// the <see cref="NpgsqlException"/> the real store throws, while the replay set, the limiter and the session
    /// store keep their real implementations.
    /// </summary>
    /// <param name="services">The host's collection.</param>
    internal static void ApplyIdentityStoreOutage(IServiceCollection services)
    {
        services.RemoveAll<ILoginIdentityStore>();
        services.AddSingleton<ILoginIdentityStore>(new FailingIdentityStore());
    }

    private sealed class InMemoryReplay(TimeProvider clock) : ILoginReplayCache
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

        public ValueTask<bool> TryConsumeAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var consumed = false;
            _used.AddOrUpdate(
                key,
                _ =>
                {
                    consumed = true;
                    return now + lifetime;
                },
                (_, expires) =>
                {
                    consumed = expires <= now;
                    return consumed ? now + lifetime : expires;
                });

            return ValueTask.FromResult(consumed);
        }
    }

    private sealed class FailingIdentityStore : ILoginIdentityStore
    {
        // The text a real dead store produces, so a case's "no database text on the wire" check has something
        // to catch instead of passing vacuously.
        public ValueTask<LoginUser> UpsertAsync(VerifiedIdentity identity, CancellationToken cancellationToken) =>
            throw new NpgsqlException("Failed to connect to 127.0.0.1:1 for evidgate_app: the identity store is unreachable");
    }

    private sealed class FailingReplay : ILoginReplayCache
    {
        public ValueTask<bool> TryConsumeAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            throw new NpgsqlException("Failed to connect to 127.0.0.1:1 for evidgate_app: the replay store is unreachable");
    }

    private sealed class InMemoryKeys : IXmlRepository
    {
        private readonly ConcurrentDictionary<string, XElement> _elements = new(StringComparer.Ordinal);

        public IReadOnlyCollection<XElement> GetAllElements() => [.. _elements.Values];

        public void StoreElement(XElement element, string friendlyName) => _elements.TryAdd(friendlyName, element);
    }
}

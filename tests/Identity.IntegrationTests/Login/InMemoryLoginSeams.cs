using System.Collections.Concurrent;
using System.Xml.Linq;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// For hosts that deliberately have no database: the key ring and the replay set live in the test process, with
/// the same first-use / live-until-expiry contract the PostgreSQL stores have (proved against the real server in
/// <c>ReplayCacheTests</c> and the Hosts shared-ring tests).
/// </summary>
internal static class InMemoryLoginSeams
{
    /// <summary>Composes the in-memory seams.</summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="clock">The clock the in-memory replay set judges its own records by.</param>
    /// <param name="includeReplay">False keeps the deployment's real replay store, for a case whose subject is
    /// that store's outage.</param>
    internal static void Apply(IServiceCollection services, TimeProvider clock, bool includeReplay = true)
    {
        if (includeReplay)
        {
            services.RemoveAll<ILoginReplayCache>();
            services.AddSingleton<ILoginReplayCache>(new InMemoryReplay(clock));
        }

        services.Configure<KeyManagementOptions>(keys => keys.XmlRepository = new InMemoryKeys());
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

    private sealed class InMemoryKeys : IXmlRepository
    {
        private readonly ConcurrentDictionary<string, XElement> _elements = new(StringComparer.Ordinal);

        public IReadOnlyCollection<XElement> GetAllElements() => [.. _elements.Values];

        public void StoreElement(XElement element, string friendlyName) => _elements.TryAdd(friendlyName, element);
    }
}

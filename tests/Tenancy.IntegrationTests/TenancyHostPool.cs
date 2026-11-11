using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One API host per variant for a whole test class, instead of one per test: building the Api composition is
/// the most expensive step of a Tenancy test, and a class that only varies the caller and the data it creates
/// does not need a fresh host for it.
/// </summary>
/// <remarks>
/// A class takes it as an <see cref="IClassFixture{TFixture}"/>. Tests of one class run one after the other,
/// so a leased host is exclusive; a second lease while the first is still out gets a private, unpooled host,
/// and so does any host over a database other than the run's shared one or built with its own configuration
/// or services. A pooled host's <c>DisposeAsync</c> only gives it back (its statement log is cleared on the
/// next lease); the pool disposes it for real when the class is done. Data isolation stays what it was:
/// every test creates its own org, project and users in the shared database.
/// </remarks>
public sealed class TenancyHostPool(PostgresTenancyFixture fixture) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Slot[] slots;
        lock (_gate)
        {
            slots = [.. _slots.Values];
            _slots.Clear();
        }

        List<Exception>? failures = null;
        foreach (var slot in slots)
        {
            try
            {
                await slot.Host.TearDownAsync();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>A pooled <see cref="TenancyApiFactory.GrantAll"/> host over <paramref name="database"/>.</summary>
    internal TenancyApiFactory GrantAll(string database) =>
        Lease("grant-all", database, () => TenancyApiFactory.GrantAll(fixture.AppConnectionString(database)));

    /// <summary>A pooled <see cref="TenancyApiFactory.RealEvaluator"/> host over <paramref name="database"/>.</summary>
    internal TenancyApiFactory RealEvaluator(string database) =>
        Lease("real-evaluator", database, () => TenancyApiFactory.RealEvaluator(fixture.AppConnectionString(database)));

    /// <summary>
    /// A pooled <see cref="TenancyApiFactory.RealResolver"/> host over <paramref name="database"/>; a host with
    /// its own configuration or services is private to the caller.
    /// </summary>
    internal TenancyApiFactory RealResolver(
        string database,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null)
    {
        var connectionString = fixture.AppConnectionString(database);

        return configuration is null && services is null
            ? Lease("real-resolver", database, () => TenancyApiFactory.RealResolver(connectionString))
            : TenancyApiFactory.RealResolver(connectionString, configuration, services);
    }

    /// <summary>
    /// A pooled <see cref="TenancyApiFactory.WithPrincipal"/> host that publishes <paramref name="principal"/>
    /// for the tenant <paramref name="org"/>/<paramref name="project"/> until it is given back; a host with its
    /// own configuration or services is private to the caller.
    /// </summary>
    internal TenancyApiFactory WithPrincipal(
        string database,
        Guid org,
        Guid project,
        IRequestPrincipal principal,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null)
    {
        var connectionString = fixture.AppConnectionString(database);
        if (configuration is not null || services is not null)
        {
            return TenancyApiFactory.WithPrincipal(connectionString, org, project, principal, configuration, services);
        }

        var host = Lease(
            "with-principal",
            database,
            () => TenancyApiFactory.WithPrincipal(connectionString, org, project, principal));
        host.Republish(org, project, principal);

        return host;
    }

    /// <summary>
    /// A pooled <see cref="TenancyApiFactory.RealResolver"/> host with in-memory <paramref name="configuration"/>;
    /// one host per distinct configuration content, so a class that always passes the same object-store settings
    /// shares one. Only for configuration a host reads at start: a host that holds per-test state (a rate
    /// limiter, a clock, a spy) stays private.
    /// </summary>
    internal TenancyApiFactory RealResolverConfigured(string database, IReadOnlyDictionary<string, string?> configuration)
    {
        var connectionString = fixture.AppConnectionString(database);

        return Lease(
            "real-resolver|" + Fingerprint(configuration),
            database,
            () => TenancyApiFactory.RealResolver(connectionString, configuration));
    }

    /// <summary>
    /// A pooled <see cref="TenancyApiFactory.WithPrincipal"/> host with in-memory <paramref name="configuration"/>
    /// (same rules as <see cref="RealResolverConfigured"/>) that publishes <paramref name="principal"/> for
    /// <paramref name="org"/>/<paramref name="project"/> until it is given back.
    /// </summary>
    internal TenancyApiFactory WithPrincipalConfigured(
        string database,
        Guid org,
        Guid project,
        IRequestPrincipal principal,
        IReadOnlyDictionary<string, string?> configuration)
    {
        var connectionString = fixture.AppConnectionString(database);
        var host = Lease(
            "with-principal|" + Fingerprint(configuration),
            database,
            () => TenancyApiFactory.WithPrincipal(connectionString, org, project, principal, configuration));
        host.Republish(org, project, principal);

        return host;
    }

    // Hashed so a secret in the configuration never appears in a slot key, a log line or a failure message.
    private static string Fingerprint(IReadOnlyDictionary<string, string?> configuration) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")))));

    private TenancyApiFactory Lease(string variant, string database, Func<TenancyApiFactory> create)
    {
        if (database != fixture.SharedName)
        {
            return create();
        }

        lock (_gate)
        {
            if (!_slots.TryGetValue(variant, out var slot))
            {
                slot = new Slot(create());
                _slots[variant] = slot;
            }
            else if (slot.Leased)
            {
                return create();
            }

            slot.Begin();
            slot.Host.Statements.Clear();

            return slot.Host;
        }
    }

    private sealed class Slot(TenancyApiFactory host)
    {
        public TenancyApiFactory Host { get; } = host;

        public bool Leased { get; set; }

        public void Begin()
        {
            Leased = true;
            var released = 0;

            // One-shot per lease: a second dispose of the same lease must not free the slot again.
            Host.Pool(() =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                {
                    Leased = false;
                }
            });
        }
    }
}

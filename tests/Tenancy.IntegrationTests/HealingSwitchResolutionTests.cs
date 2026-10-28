using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 08.6.3 AC2/AC7/AC9 without a database: the pure resolution matrix (all 27 persisted combinations plus every
/// missing-row case), the wire vocabulary, and the single-statement property of the resolution read.
/// </summary>
/// <remarks>
/// The resolver is pure by design (the feature plan's §3 "most restrictive wins"), so the matrix needs no fixture; the
/// read is asserted against a fake <see cref="ITenantDb"/> that counts statements, which is the command-counter shape
/// AC9 asks for and is cheaper than an EXPLAIN over a live plan.
/// </remarks>
public sealed class HealingSwitchResolutionTests
{
    private static readonly HealingAvailability[] Modes =
        [HealingAvailability.Enabled, HealingAvailability.Shadow, HealingAvailability.Disabled];

    /// <summary>AC2: the 27 combinations of the three storable modes resolve to their most restrictive member.</summary>
    public static TheoryData<string, string, string, string> Combinations()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var system in Modes)
        {
            foreach (var tenant in Modes)
            {
                foreach (var project in Modes)
                {
                    var expected = new[] { system, tenant, project }
                        .OrderByDescending(HealingAvailabilityResolution.Rank)
                        .First();
                    data.Add(
                        HealingAvailabilityModes.ToWire(system),
                        HealingAvailabilityModes.ToWire(tenant),
                        HealingAvailabilityModes.ToWire(project),
                        HealingAvailabilityModes.ToWire(expected));
                }
            }
        }

        return data;
    }

    /// <summary>AC2: every persisted combination, resolved through the wire vocabulary.</summary>
    [Theory]
    [MemberData(nameof(Combinations))]
    public void Persisted_combinations_resolve_to_the_most_restrictive_mode(
        string system,
        string tenant,
        string project,
        string expected)
    {
        var resolved = HealingAvailabilityResolution.Resolve(Parse(system, tenant, project));

        HealingAvailabilityModes.ToWire(resolved).ShouldBe(expected);
    }

    /// <summary>The matrix really has 27 rows (non-vacuity: 4^3 with a missing value would be 64).</summary>
    [Fact]
    public void The_matrix_covers_every_persisted_combination() =>
        Combinations().Count.ShouldBe(27);

    /// <summary>
    /// AC2: a scope without a row contributes nothing, and when no scope has one the answer is the V1 posture
    /// <c>shadow</c> — never <c>enabled</c> (which would apply fixes) and never <c>unavailable</c> (an observation).
    /// </summary>
    /// <param name="present">The persisted modes that have a row.</param>
    /// <param name="expected">The resolved mode.</param>
    [Theory]
    [InlineData(new[] { "disabled" }, "disabled")]
    [InlineData(new[] { "shadow", "disabled" }, "disabled")]
    [InlineData(new[] { "enabled", "shadow" }, "shadow")]
    [InlineData(new[] { "enabled", "enabled", "enabled" }, "enabled")]
    [InlineData(new[] { "enabled", "disabled", "shadow" }, "disabled")]
    public void Missing_rows_are_ignored_by_the_resolution(string[] present, string expected)
    {
        var persisted = present
            .Select(value => HealingAvailabilityModes.TryParse(value, out var mode) ? mode : throw new InvalidOperationException(value))
            .ToArray();

        HealingAvailabilityModes.ToWire(HealingAvailabilityResolution.Resolve(persisted)).ShouldBe(expected);
    }

    /// <summary>No row at any scope resolves to <c>shadow</c>: the V1 posture of healing that never applies (AC7).</summary>
    [Fact]
    public void No_row_at_any_scope_resolves_to_shadow()
    {
        HealingAvailabilityResolution.Resolve([]).ShouldBe(HealingAvailability.Shadow);
        HealingAvailabilityModes.ToWire(HealingAvailabilityResolution.NoRowDefault).ShouldBe(HealingAvailabilityModes.Shadow);
    }

    /// <summary>
    /// The vocabulary: only the three storable values are storable, <c>unavailable</c> parses but is never storable
    /// (AC2 — the column CHECK refuses it, and the route answers <c>invalid_switch_mode</c> for it).
    /// </summary>
    [Fact]
    public void Unavailable_is_a_mode_but_never_a_storable_one()
    {
        HealingAvailabilityModes.TryParse("unavailable", out var unavailable).ShouldBeTrue();
        unavailable.ShouldBe(HealingAvailability.Unavailable);
        HealingAvailabilityModes.IsStorable("unavailable").ShouldBeFalse();
        HealingAvailabilityModes.IsStorable(null).ShouldBeFalse();
        HealingAvailabilityModes.IsStorable(string.Empty).ShouldBeFalse();

        foreach (var mode in HealingAvailabilityModes.Storable)
        {
            HealingAvailabilityModes.IsStorable(mode).ShouldBeTrue();
            HealingAvailabilityModes.TryParse(mode, out var parsed).ShouldBeTrue();
            HealingAvailabilityModes.ToWire(parsed).ShouldBe(mode);
        }
    }

    /// <summary>Only <c>unavailable</c> outranks <c>disabled</c>: T2's observation must win over any configuration.</summary>
    [Fact]
    public void Unavailable_outranks_every_stored_mode()
    {
        HealingAvailabilityResolution.Rank(HealingAvailability.Enabled).ShouldBeLessThan(HealingAvailabilityResolution.Rank(HealingAvailability.Shadow));
        HealingAvailabilityResolution.Rank(HealingAvailability.Shadow).ShouldBeLessThan(HealingAvailabilityResolution.Rank(HealingAvailability.Disabled));
        HealingAvailabilityResolution.Rank(HealingAvailability.Disabled).ShouldBeLessThan(HealingAvailabilityResolution.Rank(HealingAvailability.Unavailable));

        HealingAvailabilityResolution.Resolve([HealingAvailability.Disabled, HealingAvailability.Unavailable])
            .ShouldBe(HealingAvailability.Unavailable);
    }

    /// <summary>
    /// AC9: the resolver reads the three scopes in ONE statement, over one table, and never writes.
    /// </summary>
    [Fact]
    public async Task The_resolver_reads_the_three_scopes_in_one_statement()
    {
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var db = new CountingTenantDb(orgId, projectId);
        var store = new HealingSwitchStore(db, new StubScopeAccessor(orgId));
        var resolver = new HealingAvailabilityResolver(store);

        var resolved = await resolver.ResolveAsync(projectId, TestContext.Current.CancellationToken);

        resolved.ShouldBe(HealingAvailability.Disabled);
        db.Queries.ShouldBe(1);
        db.Executes.ShouldBe(0);
        db.LastSql.ShouldNotBeNull();
        db.LastSql.ShouldContain("from public.healing_switch", customMessage: "the resolution reads the switch table");
        db.LastSql!.Count(c => c == ';').ShouldBe(0);
        db.LastSql.ShouldContain("scope = 'system'");
        db.LastSql.ShouldContain("scope = 'tenant'");
        db.LastSql.ShouldContain("scope = 'project'");
    }

    private static IEnumerable<HealingAvailability> Parse(params string[] modes) =>
        modes.Select(mode => HealingAvailabilityModes.TryParse(mode, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"'{mode}' is not a mode"));

    /// <summary>A tenant db that answers the resolution statement with three rows and counts the calls.</summary>
    private sealed class CountingTenantDb(Guid orgId, Guid projectId) : ITenantDb
    {
        public int Queries { get; private set; }

        public int Executes { get; private set; }

        public string? LastSql { get; private set; }

        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastSql = sql;

            object rows = new HealingSwitchRow[]
            {
                new(Guid.CreateVersion7(), "system", null, "shadow", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1),
                new(Guid.CreateVersion7(), "tenant", null, "enabled", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1),
                new(Guid.CreateVersion7(), "project", projectId, "disabled", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1),
            };

            return Task.FromResult((IReadOnlyList<T>)rows);
        }

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Executes++;
            LastSql = sql;
            return Task.FromResult(0);
        }

        public Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        internal Guid OrgId { get; } = orgId;
    }

    /// <summary>A scope accessor that opens no connection: the store only needs the org id.</summary>
    private sealed class StubScopeAccessor(Guid orgId) : ITenantScopeAccessor
    {
        public ITenantScope? Current => null;

        public ITenantScope Require() => new StubScope(orgId);

        private sealed class StubScope(Guid orgId) : ITenantScope
        {
            public Guid OrgId { get; } = orgId;

            public Guid? ProjectId => null;

            public bool IsReadOnly => true;

            public bool IsActive => true;

            public NpgsqlConnection Connection => throw new NotSupportedException();

            public NpgsqlTransaction Transaction => throw new NotSupportedException();

            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

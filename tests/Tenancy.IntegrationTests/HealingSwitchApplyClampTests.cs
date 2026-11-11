using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The V1 clamp where readers consume it: a scope that stores <c>enabled</c> resolves as <c>shadow</c> while the
/// deployment may not apply healing, and resolves as <c>enabled</c> once it may. The stored row itself never changes —
/// the read of each scope still reports what it holds, which is the difference between the mode in force and the mode
/// on record.
/// </summary>
public sealed class HealingSwitchApplyClampTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Project = Guid.CreateVersion7();

    /// <summary>
    /// <c>enabled</c> stored at one scope alone resolves as <c>shadow</c> with the clamp off and as <c>enabled</c> with
    /// it on, for each of the three scopes.
    /// </summary>
    /// <param name="scope">The scope that stores <c>enabled</c>.</param>
    [Theory]
    [InlineData(HealingSwitchScopes.System)]
    [InlineData(HealingSwitchScopes.Tenant)]
    [InlineData(HealingSwitchScopes.Project)]
    public async Task Resolve_EnabledAtOneScope_FollowsTheApplySetting(string scope)
    {
        var store = Store(Row(scope, HealingAvailabilityModes.Enabled));

        var clamped = new HealingAvailabilityResolver(store, new ApplyPolicy(enabled: false));
        var applied = new HealingAvailabilityResolver(store, new ApplyPolicy(enabled: true));

        (await clamped.ResolveAsync(Project, TestContext.Current.CancellationToken))
            .ShouldBe(HealingAvailability.Shadow);
        (await applied.ResolveAsync(Project, TestContext.Current.CancellationToken))
            .ShouldBe(HealingAvailability.Enabled);
    }

    /// <summary>A stored <c>disabled</c> is never relaxed, whatever the apply setting says.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_StoredDisabled_StaysDisabled(bool applyEnabled)
    {
        var store = Store(Row(HealingSwitchScopes.Tenant, HealingAvailabilityModes.Disabled));

        var resolver = new HealingAvailabilityResolver(store, new ApplyPolicy(applyEnabled));

        (await resolver.ResolveAsync(Project, TestContext.Current.CancellationToken))
            .ShouldBe(HealingAvailability.Disabled);
    }

    /// <summary>
    /// The switch view's <c>Resolved</c> follows the same clamp while the row it reports keeps the stored mode, so an
    /// operator can see that a scope asks for <c>enabled</c> even though the deployment holds it at <c>shadow</c>.
    /// </summary>
    [Fact]
    public async Task GetSwitch_ResolvedIsClamped_WhileTheReportedRowStaysAsStored()
    {
        var store = Store(Row(HealingSwitchScopes.Tenant, HealingAvailabilityModes.Enabled));
        var handler = new GetHealingSwitchQueryHandler(
            store,
            Substitute.For<IRequestPrincipalAccessor>(),
            Substitute.For<IRequestTenantContextAccessor>(),
            new ApplyPolicy(enabled: false));

        var result = await handler.Handle(
            new GetHealingSwitchQuery(null),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var view = result.Value;
        view.Resolved.ShouldBe(HealingAvailabilityModes.Shadow);
        view.Tenant.ShouldNotBeNull();
        view.Tenant!.Mode.ShouldBe(HealingAvailabilityModes.Enabled);
    }

    private static HealingSwitchStore Store(params HealingSwitchRow[] rows) =>
        new(new RowTenantDb(rows), new StubScopeAccessor(Org));

    private static HealingSwitchRow Row(string scope, string mode) =>
        new(
            Guid.CreateVersion7(),
            scope,
            scope == HealingSwitchScopes.Project ? Project : null,
            mode,
            null,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            1);

    /// <summary>The apply setting a case runs under.</summary>
    private sealed class ApplyPolicy(bool enabled) : IHealingApplyPolicy
    {
        public bool IsApplyEnabled => enabled;
    }

    /// <summary>A tenant db that answers the resolution read with the rows a case seeded.</summary>
    private sealed class RowTenantDb(HealingSwitchRow[] rows) : ITenantDb
    {
        public DbConnection Connection => throw new NotSupportedException();

        public DbTransaction Transaction => throw new NotSupportedException();

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<T>)(object)rows);

        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

            public void AfterCommit(Action action) => action();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

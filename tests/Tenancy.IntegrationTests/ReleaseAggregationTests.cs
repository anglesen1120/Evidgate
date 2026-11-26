// The release aggregation against a real PostgreSQL: the digest-match rule per component, the stored aggregate and its
// dedupe key. Evaluations are written straight into the table (the evaluator has its own suite), so every case states
// the exact verdicts the aggregate is computed from.
#pragma warning disable CA1861
using System.Data.Common;
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Releases;
using Evidgate.Modules.Policy.Application.Releases;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The aggregate of a release's components: digest match, no newest-run fallback, no replay, no silent pass.</summary>
[Trait("Category", "Docker")]
public sealed partial class ReleaseAggregationTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public ReleaseAggregationTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    [Fact]
    public async Task AllMatch_AllPass_ReleasePasses()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "all-pass", "active", ("web", 'a'), ("api", 'b'));
        var policy = await PolicyAsync(world, 'c');
        await BoundRunAsync(world, release, "web", 'a', policy, "PASS");
        await BoundRunAsync(world, release, "api", 'b', policy, "PASS");

        var id = await AggregateAsync(world, release);

        id.ShouldNotBeNull();
        (await ScalarAsync<string>(world, release, "decision")).ShouldBe("PASS");
        (await ScalarAsync<string>(world, release, "completeness")).ShouldBe("complete");
        (await ReasonsAsync(world, release)).ShouldBeEmpty();
        (await DecisionCountAsync(world, release)).ShouldBe(1);
    }

    [Fact]
    public async Task DigestMismatch_ComponentMissing_ReleaseNotPass()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "mismatch", "active", ("web", 'a'), ("api", 'b'));
        var policy = await PolicyAsync(world, 'c');
        await BoundRunAsync(world, release, "web", 'a', policy, "PASS");
        // The run recorded another digest for api; the row is written directly because the bind route refuses it.
        var stale = await RunAsync(world, ("api", 'z'));
        await InsertBindingAsync(world, release, stale, "api");
        await InsertEvaluationAsync(world, stale, policy, "PASS");

        await AggregateAsync(world, release);

        (await ScalarAsync<string>(world, release, "decision")).ShouldBe("INCONCLUSIVE");
        (await ScalarAsync<string>(world, release, "completeness")).ShouldBe("incomplete");
        (await ReasonsAsync(world, release)).ShouldBe(["component_digest_mismatch"]);
    }

    [Fact]
    public async Task UnboundComponent_NeverFallsBackToNewestRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "unbound", "active", ("web", 'a'), ("api", 'b'));
        var policy = await PolicyAsync(world, 'c');
        await BoundRunAsync(world, release, "web", 'a', policy, "PASS");
        // A newer run with the right digest and a PASS exists, but nothing bound it to the release.
        var unbound = await RunAsync(world, ("api", 'b'));
        await InsertEvaluationAsync(world, unbound, policy, "PASS");

        await AggregateAsync(world, release);

        (await ScalarAsync<string>(world, release, "decision")).ShouldBe("INCONCLUSIVE");
        (await ReasonsAsync(world, release)).ShouldBe(["component_run_unbound"]);
    }

    [Fact]
    public async Task SameInputs_SameHash_NoDuplicateRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "dedupe", "active", ("web", 'a'));
        var policy = await PolicyAsync(world, 'c');
        await BoundRunAsync(world, release, "web", 'a', policy, "PASS");

        var first = await AggregateAsync(world, release);
        var second = await AggregateAsync(world, release);

        second.ShouldBe(first, "the conflicting insert reads the winner back");
        (await DecisionCountAsync(world, release)).ShouldBe(1);
    }

    [Fact]
    public async Task ReplayRun_NeverBoundNorCounted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "replay", "active", ("web", 'a'), ("api", 'b'));
        var policy = await PolicyAsync(world, 'c');
        await BoundRunAsync(world, release, "web", 'a', policy, "PASS");
        var replay = await RunAsync(world, ("api", 'b'));
        await MakeReplayAsync(world, replay);
        await InsertBindingAsync(world, release, replay, "api");
        await InsertEvaluationAsync(world, replay, policy, "PASS");

        await AggregateAsync(world, release);

        (await ScalarAsync<string>(world, release, "decision")).ShouldBe("INCONCLUSIVE");
        (await ReasonsAsync(world, release)).ShouldBe(["component_run_unbound"]);
    }

    [Fact]
    public async Task OneComponentFails_ReleaseNotPass()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "fails", "active", ("web", 'a'), ("api", 'b'));
        var policy = await PolicyAsync(world, 'c');
        await BoundRunAsync(world, release, "web", 'a', policy, "PASS");
        await BoundRunAsync(world, release, "api", 'b', policy, "FAIL");

        await AggregateAsync(world, release);

        (await ScalarAsync<string>(world, release, "decision")).ShouldBe("FAIL");
        (await ReasonsAsync(world, release)).ShouldBe(["component_failed"]);
    }

    [Fact]
    public async Task MixedPolicyRevisions_ReasonAndNeverPass()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "mixed", "active", ("web", 'a'), ("api", 'b'));
        await BoundRunAsync(world, release, "web", 'a', await PolicyAsync(world, 'c'), "PASS");
        await BoundRunAsync(world, release, "api", 'b', await PolicyAsync(world, 'd'), "PASS");

        await AggregateAsync(world, release);

        (await ScalarAsync<string>(world, release, "decision")).ShouldBe("INCONCLUSIVE");
        (await ReasonsAsync(world, release)).ShouldBe(["mixed_policy_revisions"]);
    }

    [Fact]
    public async Task NoLiveVerdict_StoresNothing()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "none", "active", ("web", 'a'));
        var run = await RunAsync(world, ("web", 'a'));
        (await ReleaseIntegrationTests.BindAsync(world, release, run)).Status.ShouldBe(HttpStatusCode.Created);

        (await AggregateAsync(world, release)).ShouldBeNull();
        (await DecisionCountAsync(world, release)).ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseDecisionRows_AreImmutable()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "immutable", "active", ("web", 'a'));
        await BoundRunAsync(world, release, "web", 'a', await PolicyAsync(world, 'c'), "PASS");
        await AggregateAsync(world, release);

        var update = await Should.ThrowAsync<Npgsql.PostgresException>(() => _fixture.ExecuteAsync(
            world.Database, $"update public.release_decision set decision = 'FAIL' where org_id = '{world.Org}'"));
        var delete = await Should.ThrowAsync<Npgsql.PostgresException>(() => _fixture.ExecuteAsync(
            world.Database, $"delete from public.release_decision where org_id = '{world.Org}'"));

        update.SqlState.ShouldBe("23514");
        delete.SqlState.ShouldBe("23514");
    }

    /// <summary>The inputs read and the verdict read are one statement each, whatever the component count.</summary>
    [Fact]
    public async Task BoundedQueryCount()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var two = await ReleaseIntegrationTests.ReleaseWithAsync(world, "two", "active", ("a1", 'a'), ("a2", 'b'));
        var six = await ReleaseIntegrationTests.ReleaseWithAsync(
            world, "six", "active", ("c1", 'a'), ("c2", 'b'), ("c3", 'c'), ("c4", 'd'), ("c5", 'e'), ("c6", 'f'));
        var policy = await PolicyAsync(world, 'c');
        foreach (var (key, fill) in new[] { ("a1", 'a'), ("a2", 'b') })
        {
            await BoundRunAsync(world, two, key, fill, policy, "PASS");
        }

        foreach (var (key, fill) in new[] { ("c1", 'a'), ("c2", 'b'), ("c3", 'c'), ("c4", 'd'), ("c5", 'e'), ("c6", 'f') })
        {
            await BoundRunAsync(world, six, key, fill, policy, "PASS");
        }

        var small = await CountedAggregateAsync(world, two);
        var large = await CountedAggregateAsync(world, six);

        large.ShouldBe(small, "the statement count does not grow with the component count");
        small.ShouldBeLessThanOrEqualTo(6);
    }

    private static Task<ITenantScope> OpenScope(RunCreationTests.World world, CancellationToken cancellationToken) =>
        world.Api.Services.GetRequiredService<ITenantScopeFactory>().BeginAsync(world.Org, cancellationToken: cancellationToken);

    private static async Task<int> CountedAggregateAsync(RunCreationTests.World world, Guid release)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OpenScope(world, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeAccessor>();
        var db = new CountingTenantDb(services.ServiceProvider.GetRequiredService<ITenantDb>());
        var handler = new AggregateReleaseDecisionCommandHandler(new ReleaseAggregationReader(db, scopes), db, scopes);

        await handler.Handle(new AggregateReleaseDecisionCommand(release), cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return db.Statements;
    }

    private static async Task<Guid?> AggregateAsync(RunCreationTests.World world, Guid release)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await OpenScope(world, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var handler = services.ServiceProvider.GetRequiredService<AggregateReleaseDecisionCommandHandler>();

        var id = await handler.Handle(new AggregateReleaseDecisionCommand(release), cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return id;
    }

    /// <summary>A delegating <see cref="ITenantDb"/> counting the commands issued through it.</summary>
    private sealed class CountingTenantDb(ITenantDb inner) : ITenantDb
    {
        internal int Statements { get; private set; }

        public DbConnection Connection => inner.Connection;

        public DbTransaction Transaction => inner.Transaction;

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Statements++;
            return inner.QueryAsync<T>(sql, parameters, cancellationToken);
        }

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Statements++;
            return inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
        }

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Statements++;
            return inner.ExecuteAsync(sql, parameters, cancellationToken);
        }

        public Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Statements++;
            return inner.ScalarAsync<T>(sql, parameters, cancellationToken);
        }
    }
}

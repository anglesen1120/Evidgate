// The reader contract the policy lane consumes: the binding is computed from the release's component digests and the
// run's recorded `components`, so a mismatch is surfaced without a stored row and the same call tells the consumer
// which keys are bound.
#pragma warning disable CA1861
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IReleaseBindingReader"/> over the real host's services and a real PostgreSQL: one release of three
/// digests against a run that built two of them, one at another digest.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReleaseBindingContractTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ReleaseBindingContractTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static string Digest(char fill) => "sha256:" + new string(fill, 64);

    [Fact]
    public async Task MismatchSurfaced()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var seeded = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, database, org, 1);
        var runId = seeded.RunId;
        var revision = await _fixture.ScalarAsync<Guid>(
            database,
            $"select environment_revision_id from public.run where id = '{runId:D}'");
        var releaseId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            update public.run set components = jsonb_build_object('web', '{Digest('a')}', 'api', '{Digest('d')}')
             where id = '{runId:D}';
            insert into public.release (id, org_id, project_id, name, created_by)
            values ('{releaseId:D}', '{org:D}', '{project:D}', 'reader-mismatch', 'probe');
            insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id)
            values ('{Guid.CreateVersion7():D}', '{org:D}', '{releaseId:D}', 'web', '{Digest('a')}', '{revision:D}'),
                   ('{Guid.CreateVersion7():D}', '{org:D}', '{releaseId:D}', 'api', '{Digest('b')}', '{revision:D}'),
                   ('{Guid.CreateVersion7():D}', '{org:D}', '{releaseId:D}', 'worker', '{Digest('c')}', '{revision:D}');
            update public.release set state = 'active' where id = '{releaseId:D}';
            """);

        await using var scope = api.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var reader = provider.GetRequiredService<IReleaseBindingReader>();
        var cancellationToken = TestContext.Current.CancellationToken;

        // The reader runs on ITenantDb, which reads the ambient scope the factory opened - a published request
        // context is not a transaction, so the scope is opened here exactly as a query handler's would be.
        await using var tenant = await provider
            .GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(org, project, readOnlyTransaction: true, cancellationToken);

        // Nothing is stored for the pair yet, so the bound half is empty: the reader reports what was written, never
        // what a match would have written, and the explicit bind is the only writer.
        var boundKeys = await reader.BoundKeysAsync(releaseId, runId, cancellationToken);
        var mismatches = await reader.MismatchesAsync(releaseId, runId, cancellationToken);

        boundKeys.ShouldBeEmpty();

        var byKey = mismatches.ToDictionary(mismatch => mismatch.ComponentKey, StringComparer.Ordinal);
        byKey.Keys.ShouldBe(["api", "worker"], ignoreOrder: true);
        byKey["api"].ShouldBe(new ReleaseMismatch("api", "digest_mismatch", Digest('b'), Digest('d')));
        byKey["worker"].ShouldBe(new ReleaseMismatch("worker", "component_missing", Digest('c'), null));

        // The reader persists nothing: the pair held no binding row while it derived the answer.
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.release_run where org_id = '{org:D}'")).ShouldBe(0);

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.release_run (id, org_id, release_id, run_id, component_key)
            values ('{Guid.CreateVersion7():D}', '{org:D}', '{releaseId:D}', '{runId:D}', 'web');
            """);

        (await reader.BoundKeysAsync(releaseId, runId, cancellationToken)).ShouldBe(["web"]);
        (await reader.MismatchesAsync(releaseId, runId, cancellationToken))
            .Select(mismatch => mismatch.ComponentKey).ShouldBe(["api", "worker"]);

        await api.DisposeAsync();
    }
}

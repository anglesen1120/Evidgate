using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Domain;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One tenant with a project and the host's real credit writer driven inside writable tenant scopes, so the writer's
/// "the open scope's organization" guard, the usage ledger and the audit unit run as they do in a request.
/// </summary>
internal sealed record AiCreditTestWorld(
    TenancyApiFactory Api,
    HttpClient Client,
    PostgresTenancyFixture Fixture,
    string Database,
    Guid User,
    Guid Org,
    Guid Project) : IAsyncDisposable
{
    internal static async Task<AiCreditTestWorld> StartAsync(PostgresTenancyFixture fixture)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new AiCreditTestWorld(api, client, fixture, database, user, org, project);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Api.DisposeAsync();
    }

    /// <summary>Runs <paramref name="call"/> on the host's writer in one writable scope, committing unless told not to.</summary>
    internal async Task<T> InScopeAsync<T>(
        Func<AiCreditWriter, CancellationToken, ValueTask<T>> call,
        bool commit = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(Org, Project, false, cancellationToken);

        var result = await call(services.ServiceProvider.GetRequiredService<AiCreditWriter>(), cancellationToken);
        if (commit)
        {
            await scope.CommitAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>Runs <paramref name="call"/> on a writer built with <paramref name="ledger"/> in place of the host's ledger.</summary>
    internal async Task<T> InScopeWithLedgerAsync<T>(
        IUsageLedger ledger,
        Func<AiCreditWriter, CancellationToken, ValueTask<T>> call)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>().BeginAsync(Org, Project, false, cancellationToken);

        var writer = new AiCreditWriter(
            provider.GetRequiredService<ITenantDb>(),
            provider.GetRequiredService<ITenantScopeAccessor>(),
            ledger,
            provider.GetRequiredService<IAuditTrail>(),
            provider.GetRequiredService<IRequestPrincipalAccessor>(),
            TimeProvider.System);

        var result = await call(writer, cancellationToken);
        await scope.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>Records one ledger row through the host's ledger, so a test can occupy a billing key.</summary>
    internal async Task RecordLedgerAsync(UsageEntryInput entry)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(Org, Project, false, cancellationToken);

        await services.ServiceProvider.GetRequiredService<IUsageLedger>()
            .RecordAsync(new RecordUsageBatchRequest([entry]), cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>A healing call of this tenant's project, written by the owner role, and its id.</summary>
    internal async Task<Guid> SeedCallAsync(Guid? org = null, Guid? project = null)
    {
        var id = Guid.CreateVersion7();
        await Fixture.ExecuteAsync(
            Database,
            $"""
            insert into public.healing_llm_call (id, org_id, project_id, purpose, model, prompt_version, status)
            values ('{id:D}', '{org ?? Org:D}', '{project ?? Project:D}', 'authoring_generation', 'test-model', 'v1', 'ok')
            """);

        return id;
    }

    /// <summary>A call-less entry with a fresh replay key.</summary>
    internal static CreditEntryRequest CallLess(CreditEntryKind kind, long amount, string? key = null, string? reason = null) =>
        new(kind, amount, null, key ?? Guid.NewGuid().ToString("N"), reason);

    /// <summary>A call-bound entry.</summary>
    internal static CreditEntryRequest ForCall(CreditEntryKind kind, long amount, Guid call) =>
        new(kind, amount, call, null, null);

    /// <summary>Appends <paramref name="request"/> in its own committed scope.</summary>
    internal Task<Result<CreditAppendOutcome>> AppendAsync(CreditEntryRequest request) =>
        InScopeAsync((writer, token) => writer.AppendAsync(Org, request, token));

    /// <summary>Appends an entry that must succeed.</summary>
    internal async Task<CreditAppendOutcome> AppendedAsync(CreditEntryRequest request)
    {
        var result = await AppendAsync(request);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return result.Value;
    }

    internal Task<long> CountAsync(string table) =>
        Fixture.ScalarAsync<long>(Database, $"select count(*) from public.{table} where org_id = '{Org:D}'");
}

// Test seams of the 05.2.1-T1 run-create suite: a crashing outbox writer (AC2) and the direct
// principal/tenant seam the CI-claims cases need (the header seam of TenancyApiFactory carries no CiRun).
#pragma warning disable CA1861
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC2's crash: the outbox write throws after the run, the planned executions and the producer slots were written, so
/// the tenant transaction must roll all four back. Registered over the real writer through
/// <c>WithWebHostBuilder(...ConfigureTestServices)</c>, so the rest of the pipeline is production.
/// </summary>
internal sealed class ThrowingOutboxWriter : IOutboxWriter
{
    /// <inheritdoc />
    public Task<Guid> EnqueueAsync(
        string taskType,
        string payloadJson,
        string aggregateType,
        Guid aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated crash after slot registration, before commit");
}

/// <summary>
/// Failure injection at one write of the create transaction: the tenant Dapper path throws when the statement carries
/// <paramref name="marker"/>, so the rollback can be proven at each step (AC2). Every other statement passes through.
/// </summary>
internal sealed class ThrowingTenantDb(ITenantDb inner, string marker) : ITenantDb
{
    /// <inheritdoc />
    public System.Data.Common.DbConnection Connection => inner.Connection;

    /// <inheritdoc />
    public System.Data.Common.DbTransaction Transaction => inner.Transaction;

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        inner.QueryAsync<T>(sql, parameters, cancellationToken);

    /// <inheritdoc />
    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        sql.Contains(marker, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"simulated crash at '{marker}', before commit")
            : inner.ExecuteAsync(sql, parameters, cancellationToken);

    /// <inheritdoc />
    public Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        inner.ScalarAsync<T>(sql, parameters, cancellationToken);
}

/// <summary>Failure injection at the producer-slot registration: the run and its planned executions are already written.</summary>
internal sealed class ThrowingProducerRegistry(IProducerRegistry inner) : IProducerRegistry
{
    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<ProducerSlotRef>>> RegisterSlotsAsync(
        Guid runId,
        int shardCount,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("simulated crash at the producer-slot registration, before commit");

    /// <inheritdoc />
    public ValueTask<Result<ProducerSlotBinding>> BindOrCheckAsync(
        Guid runId,
        string producerId,
        string tokenJti,
        string? ciRunRef,
        CancellationToken cancellationToken) =>
        inner.BindOrCheckAsync(runId, producerId, tokenJti, ciRunRef, cancellationToken);

    /// <inheritdoc />
    public ValueTask<Result<ProducerSlotClosure>> CloseSlotAsync(
        Guid runId,
        string producerId,
        string tokenJti,
        string? ciRunRef,
        long declaredLastSequence,
        int declaredCount,
        CancellationToken cancellationToken) =>
        inner.CloseSlotAsync(runId, producerId, tokenJti, ciRunRef, declaredLastSequence, declaredCount, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ProducerSlotView>> GetRunProducerSlotsAsync(Guid runId, CancellationToken cancellationToken) =>
        inner.GetRunProducerSlotsAsync(runId, cancellationToken);
}

/// <summary>The principal/tenant seam of the CI-claims cases: publish a caller the headers cannot express.</summary>
internal static class RunCreationSeams
{
    /// <summary>
    /// Sends <see cref="CreateRunCommand"/> through the real C2 pipeline with <paramref name="principal"/> published
    /// for the flow — the same seam <c>TenancyApiFactory</c>'s middleware uses, invoked directly because a
    /// <c>CiRunReference</c> has no header.
    /// </summary>
    internal static async Task<Result<RunCreated>> SendDirectAsync(
        TenancyApiFactory api,
        Guid org,
        Guid project,
        IRequestPrincipal principal,
        string body,
        string? idempotencyKey = null)
    {
        await using var services = api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenantScope = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(org, project);
        using var principalScope = provider.GetRequiredService<IRequestPrincipalSetter>().Publish(principal);

        return await provider.GetRequiredService<IMediator>()
            .Send(
                new CreateRunCommand(project, Encoding.UTF8.GetBytes(body), idempotencyKey ?? RegistryApi.NewKey()),
                TestContext.Current.CancellationToken);
    }
}

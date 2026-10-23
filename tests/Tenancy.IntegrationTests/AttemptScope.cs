using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2: one tenant transaction the test opens itself — <see cref="ITenantScopeFactory.BeginAsync"/>
/// over the real host's app-role data source — with the writer and the blocked-dependant resolver resolved
/// from the same container. The writer never opens or commits a transaction, so the test owns the commit
/// boundary (AC5): <see cref="CommitAsync"/> makes the rows durable, disposing without it rolls them back.
/// </summary>
internal sealed class AttemptScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _services;
    private readonly ITenantScope _scope;

    private AttemptScope(AsyncServiceScope services, ITenantScope scope)
    {
        _services = services;
        _scope = scope;
        Writer = services.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();
        Resolver = services.ServiceProvider.GetRequiredService<IBlockedAttemptResolver>();
    }

    /// <summary>The writer under test, running in this transaction.</summary>
    internal ICaseAttemptWriter Writer { get; }

    /// <summary>The blocked-dependant resolver under test, running in this transaction.</summary>
    internal IBlockedAttemptResolver Resolver { get; }

    /// <summary>True once the transaction committed; false while it is open or after a rollback.</summary>
    internal bool Committed { get; private set; }

    /// <summary>
    /// Opens the tenant transaction for <paramref name="org"/> (optionally narrowed to a project).
    /// <para>
    /// Deliberately <b>not</b> <c>async</c> (tests/README.md, "AsyncLocal rule"): the factory publishes the scope
    /// into its ambient <c>AsyncLocal</c> slot synchronously, in the caller's execution context, and
    /// <c>ExecutionContext</c> flows only downwards. An <c>async</c> wrapper would strand the publication in its
    /// own frame, so the writer and the resolver — which resolve the scope through that slot — would fail with
    /// <c>TenantScopeMissingException</c> on a scope that is open.
    /// </para>
    /// </summary>
    internal static Task<AttemptScope> BeginAsync(TenancyApiFactory api, Guid org, Guid? project = null)
    {
        var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

        // Continued rather than awaited, for the reason above: the continuation only builds the holder and needs
        // no ambient context of its own.
        return scopes
            .BeginAsync(org, project, false, TestContext.Current.CancellationToken)
            .ContinueWith(
                opened => new AttemptScope(services, opened.GetAwaiter().GetResult()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>Persists one batch of attempts in this transaction.</summary>
    internal Task<IReadOnlyList<CaseAttemptWriteOutcome>> WriteAsync(Guid runId, params CaseAttemptInput[] attempts) =>
        Writer.WriteAttemptsAsync(runId, attempts, TestContext.Current.CancellationToken).AsTask();

    /// <summary>Resolves the dependants of one failed setup in this transaction.</summary>
    internal Task<IReadOnlyList<string>> ResolveAsync(Guid runId, string failedExecutionKey) =>
        Resolver.ResolveDependantsAsync(runId, failedExecutionKey, TestContext.Current.CancellationToken).AsTask();

    /// <summary>The C2 commit boundary; the writer itself never commits.</summary>
    internal async Task CommitAsync()
    {
        await _scope.CommitAsync(TestContext.Current.CancellationToken);
        Committed = true;
    }

    /// <summary>Disposes the transaction scope (a rollback when <see cref="CommitAsync"/> was not called) and the container scope.</summary>
    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}

using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One caller-owned tenant transaction over the real host's container: a module contract resolved from
/// <see cref="Services"/> runs inside exactly this transaction, so a test owns the commit boundary and can show that a
/// read sees the rows this scope wrote and that nothing was committed behind its back.
/// </summary>
internal sealed class ContractScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _services;
    private readonly ITenantScope _scope;

    private ContractScope(AsyncServiceScope services, ITenantScope scope)
    {
        _services = services;
        _scope = scope;
    }

    /// <summary>The host container, which also publishes this scope as the ambient one.</summary>
    internal IServiceProvider Services => _services.ServiceProvider;

    /// <summary>True once <see cref="CommitAsync"/> succeeded; a disposal without it rolls back.</summary>
    internal bool Committed { get; private set; }

    /// <summary>Resolves a service that must run in this transaction.</summary>
    internal T Resolve<T>()
        where T : notnull => _services.ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// Opens the tenant transaction for <paramref name="org"/> (optionally narrowed to a project).
    /// <para>
    /// Deliberately <b>not</b> <c>async</c> (tests/README.md, "AsyncLocal rule"): the factory publishes the scope into its
    /// ambient <c>AsyncLocal</c> slot synchronously, in the caller's execution context, and an <c>async</c> wrapper would
    /// strand that publication in its own frame, so a contract resolved through the slot would report a missing scope.
    /// </para>
    /// </summary>
    internal static Task<ContractScope> BeginAsync(
        TenancyApiFactory api,
        Guid org,
        Guid? project = null,
        bool readOnly = false)
    {
        var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

        return scopes
            .BeginAsync(org, project, readOnly, TestContext.Current.CancellationToken)
            .ContinueWith(
                opened => new ContractScope(services, opened.GetAwaiter().GetResult()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>The caller's commit boundary; disposes to roll back instead.</summary>
    internal async Task CommitAsync()
    {
        await _scope.CommitAsync(TestContext.Current.CancellationToken);
        Committed = true;
    }

    /// <summary>Ends the transaction (a rollback unless committed) and the container scope.</summary>
    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}

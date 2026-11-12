using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// Runs one unit of the gateway's database work in its own short tenant transaction. The gateway uses it for every phase
/// (guard read, reservation, settlement) so no transaction is open while the provider is called: a tenant scope holds a
/// pooled connection and a row lock, and a provider call can take a minute.
/// </summary>
internal sealed class HealingLlmTransactions(ITenantScopeFactory scopes)
{
    /// <summary>Opens a scope for <paramref name="orgId"/>, runs <paramref name="work"/> in it and commits.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="orgId">The tenant the work belongs to; the caller took it from its own authenticated context.</param>
    /// <param name="projectId">The project the scope is bound to.</param>
    /// <param name="work">The statements to run on the ambient tenant path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidOperationException">A tenant scope is already open in this async flow.</exception>
    internal async Task<T> RunAsync<T>(
        Guid orgId,
        Guid projectId,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (scopes.Current is not null)
        {
            // A scope held across the provider call is exactly what the two-phase shape exists to prevent.
            throw new InvalidOperationException(
                "the healing LLM gateway opens its own short tenant transactions and must be called outside a tenant scope");
        }

        await using var scope = await scopes.BeginAsync(orgId, projectId, false, cancellationToken).ConfigureAwait(false);
        var result = await work(cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

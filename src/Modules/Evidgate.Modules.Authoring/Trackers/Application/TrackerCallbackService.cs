using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;

namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>
/// The browser's return from a tracker's consent page. Each Mediator message opens its own tenant transaction, so the
/// provider call sits between two short units and never inside one: take the consent, exchange the code, then store.
/// </summary>
/// <param name="mediator">Sends the two units.</param>
/// <param name="provider">The provider clients.</param>
internal sealed class TrackerCallbackService(IMediator mediator, ITrackerProviderClient provider)
{
    /// <summary>Runs the callback.</summary>
    /// <param name="wireProvider">The provider segment of the route.</param>
    /// <param name="code">The authorization code query value.</param>
    /// <param name="state">The state query value.</param>
    /// <param name="providerError">The provider's <c>error</c> query value, when the user declined.</param>
    /// <param name="cancellationToken">Cancels the callback.</param>
    /// <returns>The new connection, or the error to answer.</returns>
    public async ValueTask<Result<TrackerConnectionDto>> CompleteAsync(
        string? wireProvider,
        string? code,
        string? state,
        string? providerError,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(TrackerCallbackService));

        if (!TrackerProviders.TryParse(wireProvider, out var kind))
        {
            return Result.Failure<TrackerConnectionDto>(TrackerErrors.ConnectionNotFound);
        }

        // The first unit checks the permission, then the shape and caps of every query value, then takes the consent.
        var taken = await mediator
            .Send(new TakeTrackerConsentCommand(kind, state, code, providerError), cancellationToken)
            .ConfigureAwait(false);
        if (!taken.IsSuccess)
        {
            return Result.Failure<TrackerConnectionDto>(taken.Error!);
        }

        if (!string.IsNullOrEmpty(providerError))
        {
            // The state is spent and nothing is stored: a declined consent cannot be completed later.
            return Result.Failure<TrackerConnectionDto>(TrackerErrors.ConsentDenied);
        }

        // No transaction is open here: the provider call holds no connection and no row lock.
        var token = await provider.ExchangeAsync(kind, code!, taken.Value.PkceVerifier, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Result.Failure<TrackerConnectionDto>(TrackerErrors.ExchangeFailed);
        }

        return await mediator.Send(new CompleteTrackerConnectionCommand(taken.Value, token), cancellationToken).ConfigureAwait(false);
    }
}

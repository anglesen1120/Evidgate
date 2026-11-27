using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>The refusals of the tracker routes that more than one handler answers.</summary>
internal static class TrackerErrors
{
    /// <summary>400 for a state that is unknown, expired, replayed, malformed or another provider's; one body for all, so none is an oracle.</summary>
    internal static Error ConsentInvalid => new(
        ApiErrorCode.TrackerConsentInvalid,
        "the consent is unknown, expired, already used or for another tracker; start the connection again",
        400);

    /// <summary>400 when the user declined at the tracker.</summary>
    internal static Error ConsentDenied => new(
        ApiErrorCode.TrackerConsentDenied,
        "the consent was declined at the tracker; nothing was connected",
        400);

    /// <summary>400 when the tracker refused the code.</summary>
    internal static Error ExchangeFailed => new(
        ApiErrorCode.TrackerExchangeFailed,
        "the tracker did not accept the authorization code; start the connection again",
        400);

    /// <summary>404 for a connection that does not exist in this project, whoever owns it.</summary>
    internal static Error ConnectionNotFound => Error.NotFound("no tracker connection with that id is visible in this project");
}

/// <summary>Store views to the wire shape; the only mapping, so no token field can be added to a response by accident.</summary>
internal static class TrackerConnectionMapping
{
    /// <summary>The wire form of <paramref name="view"/>.</summary>
    internal static TrackerConnectionDto ToDto(TrackerConnectionView view) => new(
        view.Id,
        TrackerProviders.ToWire(view.Provider),
        TrackerProviders.ToWire(view.State),
        new TrackerCapabilitiesDto(view.CreateIssueEnabled),
        view.GrantedScopes,
        view.CreatedAt);
}

using System.Text.Json;
using Polly;

namespace Evidgate.Modules.Identity.Login.Google;

/// <summary>
/// One rule (02.3.1-T1.2 / AC8) for "the provider could not answer": every Google call maps a failure to
/// <see cref="LoginFailure.ProviderUnavailable"/> (503 <c>unavailable</c> plus <c>Retry-After</c>) with this
/// predicate, so the JWKS cache, the token validator and the token exchange cannot drift apart and let a
/// provider failure escape as a 500.
/// </summary>
/// <remarks>
/// The JWKS client's resilience pipeline enforces its budgets with Polly, whose timeout surfaces as
/// <c>Polly.Timeout.TimeoutRejectedException</c>. That type derives from
/// <see cref="ExecutionRejectedException"/> and from nothing else this list would otherwise catch: it is
/// not a <see cref="TimeoutException"/>, not an <see cref="HttpRequestException"/> and not an
/// <see cref="OperationCanceledException"/>, so a plain catch list lets a pipeline timeout fail the
/// request with a 500 and skips the refresh-window backoff.
/// </remarks>
internal static class GoogleProviderFailure
{
    /// <summary>
    /// True when <paramref name="exception"/> is a provider-side failure: a transport error, a timeout of
    /// ours or of the resilience pipeline, or a document the provider sent that cannot be used.
    /// </summary>
    /// <param name="exception">The exception a Google call threw.</param>
    /// <param name="callerToken">The token the caller passed in, which decides what a cancellation means.</param>
    internal static bool IsUnavailable(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Cancellation is a provider outage only when the caller did not ask for it: a caller that
            // cancelled is a client that left, and the request is already over.
            OperationCanceledException => !callerToken.IsCancellationRequested,

            // Includes Polly's TimeoutRejectedException (a pipeline timeout) through its base type.
            HttpRequestException or TimeoutException or ExecutionRejectedException => true,

            // A body that is not the JSON we expect, or a request the allowlist refused.
            JsonException or InvalidOperationException or ArgumentException or FormatException => true,

            _ => false,
        };
    }
}

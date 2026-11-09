using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace Evidgate.Testing;

/// <summary>
/// Bounds and pacing of <see cref="TransientS3Retry.RunAsync"/>. The defaults suit a freshly started
/// SeaweedFS: a healthy start accepts its first write within seconds, so the budget is only spent on a
/// start that really is unhealthy.
/// </summary>
/// <param name="Budget">Wall-clock ceiling for all attempts together.</param>
/// <param name="AttemptTimeout">Ceiling of one attempt; AWSSDK v4 has no settable request timeout.</param>
/// <param name="Delay">Pause before the first retry; it doubles per attempt up to <paramref name="MaxDelay"/>.</param>
/// <param name="MaxDelay">Ceiling of the pause between two attempts.</param>
/// <param name="MaxAttempts">Ceiling on the number of attempts, whatever time is left in the budget.</param>
public sealed record TransientS3RetryOptions(
    TimeSpan Budget,
    TimeSpan AttemptTimeout,
    TimeSpan Delay,
    TimeSpan MaxDelay,
    int MaxAttempts)
{
    /// <summary>The first-write window of a starting SeaweedFS container.</summary>
    public static TransientS3RetryOptions Startup { get; } = new(
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(4),
        MaxAttempts: 40);
}

/// <summary>
/// The one bounded, transient-only retry for the first S3 calls against a starting store. A starting
/// SeaweedFS is not writable yet (the master is still electing itself and S3 calls are refused with a 5xx
/// or a reset connection), so a fixture's first bucket create / probe write retries here; every later call
/// stays an ordinary call.
/// </summary>
public static class TransientS3Retry
{
    private const string Region = "us-east-1";

    /// <summary>
    /// Runs <paramref name="step"/> until it succeeds, retrying only what a starting store produces, each
    /// attempt bounded by its own cancellation token. An auth, 404 or other 4xx answer is a real fault and is
    /// rethrown at once, so a wrong credential or a missing bucket is never retried away. When the budget or the
    /// attempt cap is spent, an <see cref="InvalidOperationException"/> carries the last error and, when given,
    /// <paramref name="describeFailure"/>'s text (the container log tail) so the next flake names its own cause.
    /// </summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> step,
        TransientS3RetryOptions options,
        Func<Task<string>>? describeFailure = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(options);

        var deadline = DateTime.UtcNow + options.Budget;
        var delay = options.Delay;
        Exception? lastError = null;

        for (var attemptNumber = 1; ; attemptNumber++)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || attemptNumber > options.MaxAttempts)
            {
                var detail = describeFailure is null ? string.Empty : $" {await describeFailure()}";
                throw new InvalidOperationException(
                    $"the store refused its first writes ({attemptNumber - 1} attempts, budget "
                    + $"{(int)options.Budget.TotalSeconds}s; last error: "
                    + $"{(lastError is null ? "none" : lastError.GetType().Name + ": " + lastError.Message)})."
                    + detail,
                    lastError);
            }

            using var attempt = new CancellationTokenSource(
                remaining < options.AttemptTimeout ? remaining : options.AttemptTimeout);

            try
            {
                await step(attempt.Token);
                return;
            }
            catch (Exception exception) when (IsTransient(exception, attempt.IsCancellationRequested))
            {
                lastError = exception;
            }

            // No pause after the last allowed attempt, and never past the budget.
            var pause = TimeSpan.FromTicks(Math.Min(delay.Ticks, (deadline - DateTime.UtcNow).Ticks));
            if (attemptNumber < options.MaxAttempts && pause > TimeSpan.Zero)
            {
                await Task.Delay(pause);
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks, options.MaxDelay.Ticks / 2) * 2);
        }
    }

    /// <summary>
    /// True for the failures a starting store produces and never for a real fault: a 5xx or 408 answer, the
    /// transport failures AWSSDK v4 throws here (<see cref="HttpRequestException"/> for a refused connection,
    /// <see cref="IOException"/> for one closed mid-request), and the attempt's own timeout.
    /// <see cref="OperationCanceledException"/> counts only when the loop's own per-attempt token fired, so a
    /// cancelled run is never retried away.
    /// </summary>
    public static bool IsTransient(Exception exception, bool attemptTimedOut) =>
        exception switch
        {
            OperationCanceledException => attemptTimedOut,
            AmazonServiceException service => service.StatusCode == HttpStatusCode.RequestTimeout
                || (int)service.StatusCode >= 500,
            // A status-bearing HttpRequestException follows the same 408/5xx rule as an S3 answer.
            HttpRequestException { StatusCode: { } status } => status == HttpStatusCode.RequestTimeout || (int)status >= 500,
            HttpRequestException or IOException or WebException => true,
            _ => false,
        };

    /// <summary>
    /// A client for the retry loop: no SDK-side retries and no SDK-side waits, so the loop's per-attempt
    /// token and <see cref="TransientS3RetryOptions"/> are the whole bound.
    /// </summary>
    public static AmazonS3Client CreateClient(string serviceUrl, string accessKey, string secretKey)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = Region,
            MaxErrorRetry = 0,
        };

        return new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Evidgate.Testing;

/// <summary>
/// Bounds of <see cref="ContainerStart"/>. One Docker API call (create, start) is cut off by the Docker
/// client after 100 s; on a loaded host a single stalled call used to fail every test of the class whose
/// fixture started the container, so a start gets a few bounded attempts inside one wall-clock budget.
/// </summary>
/// <param name="MaxAttempts">Ceiling on the number of create+start attempts.</param>
/// <param name="AttemptTimeout">Ceiling of one attempt, wait strategy included.</param>
/// <param name="Budget">Wall-clock ceiling for all attempts together.</param>
/// <param name="Delay">Pause between two attempts.</param>
/// <param name="DisposeTimeout">Ceiling for disposing the container of a failed attempt.</param>
public sealed record ContainerStartOptions(
    int MaxAttempts,
    TimeSpan AttemptTimeout,
    TimeSpan Budget,
    TimeSpan Delay,
    TimeSpan DisposeTimeout)
{
    /// <summary>Three attempts, none past three minutes, all inside five (a fixture token shorter than that still wins).</summary>
    public static ContainerStartOptions Default { get; } = new(
        MaxAttempts: 3,
        AttemptTimeout: TimeSpan.FromMinutes(3),
        Budget: TimeSpan.FromMinutes(5),
        Delay: TimeSpan.FromSeconds(2),
        DisposeTimeout: TimeSpan.FromSeconds(30));
}

/// <summary>
/// The one container start every fixture uses: it builds a fresh container per attempt, retries only the
/// Docker API failures a loaded host produces (a timed-out or reset call, a 5xx answer), disposes the
/// half-created container before the next attempt and logs each retry with the failing call. An image that
/// does not exist, a 4xx answer, a wait-strategy timeout or a caller's cancellation is a real fault and
/// surfaces at once.
/// </summary>
public static partial class ContainerStart
{
    /// <summary>Builds, starts and returns a container; on the final failure nothing is left running.</summary>
    public static Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> create,
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
        where TContainer : IContainer =>
        StartWithReaperAsync(create, options, log, cancellationToken);

    /// <summary>The delegate form of <see cref="StartAsync{TContainer}"/>, so the loop is testable without Docker.</summary>
    public static async Task<T> StartAsync<T>(
        Func<T> create,
        Func<T, CancellationToken, Task> start,
        Func<T, ValueTask> dispose,
        ContainerStartOptions? options = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(dispose);
        options ??= ContainerStartOptions.Default;
        log ??= static message => Console.Error.WriteLine(message);

        var deadline = DateTime.UtcNow + options.Budget;
        ExceptionDispatchInfo? failure = null;
        for (var attempt = 1; ; attempt++)
        {
            // A delay that overshoots the deadline must surface the Docker failure, not a negative timeout.
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                failure?.Throw();
            }

            using var attemptTimeout = new CancellationTokenSource(
                remaining < options.AttemptTimeout ? remaining : options.AttemptTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);

            var container = create();
            try
            {
                await start(container, linked.Token).ConfigureAwait(false);
                return container;
            }
            catch (Exception exception)
            {
                var cleanupBudget = deadline - DateTime.UtcNow;
                var cleanupTimeout = cleanupBudget < options.DisposeTimeout ? cleanupBudget : options.DisposeTimeout;
                await DisposeQuietlyAsync(
                    container,
                    dispose,
                    cleanupTimeout > TimeSpan.Zero ? cleanupTimeout : TimeSpan.Zero,
                    log).ConfigureAwait(false);

                var last = attempt >= options.MaxAttempts || deadline - DateTime.UtcNow <= options.Delay;
                if (last || cancellationToken.IsCancellationRequested || !IsTransient(exception))
                {
                    throw;
                }

                log(
                    $"[container-start] attempt {attempt}/{options.MaxAttempts} failed in "
                    + $"{FailingCall(exception)}: {exception.GetType().Name}: {exception.Message}; retrying in "
                    + $"{options.Delay.TotalSeconds:0.#}s");
                failure = ExceptionDispatchInfo.Capture(exception);
            }

            await Task.Delay(options.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True for a failure of the Docker API itself under load: a call cut off by its timeout, a reset or
    /// refused connection, a 408/5xx answer. A 4xx answer (missing image, bad config) is never transient,
    /// and a fatal link anywhere in the chain wins over a transient one.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var transient = false;
        foreach (var link in Chain(exception))
        {
            switch (link)
            {
                case DockerApiException api when IsClientError(api.StatusCode):
                    return false;
                case DockerApiException api when (int)api.StatusCode >= 500 || api.StatusCode == HttpStatusCode.RequestTimeout:
                    transient = true;
                    break;
                case FileNotFoundException or DirectoryNotFoundException:
                    return false;
                case OperationCanceledException or HttpRequestException or IOException or SocketException:
                    transient = true;
                    break;
            }
        }

        return transient;
    }

    /// <summary>
    /// Disposes a fixture's container. On a loaded host dockerd can stall a remove past the Docker client's
    /// timeout; the reaper removes the container when the process ends, so a timed-out or otherwise transient
    /// dispose is logged and swallowed, but only while the reaper runs. Any other failure surfaces.
    /// </summary>
    public static Task DisposeAsync<TContainer>(TContainer? container, TimeSpan? timeout = null, Action<string>? log = null)
        where TContainer : class, IAsyncDisposable =>
        container is null
            ? Task.CompletedTask
            : DisposeAsync(
                container,
                static c => c.DisposeAsync(),
                static () => TestcontainersSettings.ResourceReaperEnabled && !ResourceReaper.IsUnavailable,
                timeout,
                log);

    /// <summary>The delegate form of <see cref="DisposeAsync{TContainer}"/>, so the rules are testable without Docker.</summary>
    public static async Task DisposeAsync<T>(
        T container,
        Func<T, ValueTask> dispose,
        Func<bool> reaperRunning,
        TimeSpan? timeout = null,
        Action<string>? log = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(dispose);
        ArgumentNullException.ThrowIfNull(reaperRunning);
        log ??= static message => Console.Error.WriteLine(message);
        try
        {
            var pending = dispose(container).AsTask();
            // A dispose still running after the wait must not surface as an unobserved fault later.
            _ = pending.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            await pending.WaitAsync(timeout ?? DisposeBudget).ConfigureAwait(false);
        }
        catch (Exception exception) when (reaperRunning() && (exception is TimeoutException || IsTransient(exception)))
        {
            log($"[container-stop] {exception.GetType().Name}: {exception.Message}; "
                + $"{container.GetType().Name} left for Ryuk");
        }
    }

    /// <summary>Two minutes: two Docker client timeouts' worth of waiting on one remove.</summary>
    private static readonly TimeSpan DisposeBudget = TimeSpan.FromMinutes(2);

    private static bool IsClientError(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout;

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> inner = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is null ? Array.Empty<Exception>() : [exception.InnerException];
        foreach (var link in inner.SelectMany(Chain))
        {
            yield return link;
        }
    }

    /// <summary>Names the Docker call from the stack: create and start are separate API calls.</summary>
    private static string FailingCall(Exception exception)
    {
        var text = exception.ToString();
        if (text.Contains("CreateContainerAsync", StringComparison.Ordinal))
        {
            return "create";
        }

        if (text.Contains("StartContainerAsync", StringComparison.Ordinal))
        {
            return "start";
        }

        return text.Contains("DockerClient", StringComparison.Ordinal) ? "docker-api" : "start";
    }

    private static async Task DisposeQuietlyAsync<T>(
        T container,
        Func<T, ValueTask> dispose,
        TimeSpan timeout,
        Action<string> log)
    {
        try
        {
            await dispose(container).AsTask().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A stalled Docker also stalls the cleanup; the resource reaper removes what is left.
            log($"[container-start] disposing the failed container did not finish: {exception.GetType().Name}");
        }
    }
}

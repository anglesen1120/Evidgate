using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// Builds the <see cref="ContractSchemaSet"/> singleton in the background once the host starts, so the first
/// body-validating request never pays the Roslyn compile of every schema (tens of seconds for the first load of a
/// process) inside an open database transaction.
/// <para>
/// It resolves the singleton through the container instead of calling <see cref="ContractSchemaSet.Load"/>:
/// the lazy registration stays the only place that locates the contracts directory, and a request that
/// arrives while the warm-up runs waits on the container's singleton lock instead of compiling a second
/// copy. A failure is logged and kept in <see cref="Completion"/>; the container caches no failed singleton,
/// so the request path retries and throws the exception it always did.
/// </para>
/// </summary>
public sealed partial class ContractSchemaWarmUpService : IHostedService, IDisposable
{
    // Hosts that start together in one process (test factories) would otherwise miss the validator's
    // process-wide cache at the same time and each compile every schema.
    private static readonly Lock ColdLoad = new();

    private readonly IServiceProvider _services;
    private readonly ILogger<ContractSchemaWarmUpService> _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    // the host and the container it owns stop this service in either order, so the cancel and the dispose of
    // _stopping have to be one step: a CTS disposed under a concurrent cancel throws ObjectDisposedException.
    private readonly Lock _lifecycle = new();
    private Task? _run;
    private bool _disposed;

    /// <summary>Creates the service over the container that owns the schema set.</summary>
    public ContractSchemaWarmUpService(IServiceProvider services, ILogger<ContractSchemaWarmUpService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _logger = logger;
    }

    /// <summary>Completes when the schema set is built, faults with the load exception when it is not, and is cancelled when the host stops first.</summary>
#pragma warning disable VSTHRD003 // The source is this service's own completion source, not a task started elsewhere.
    public Task Completion => _completion.Task;
#pragma warning restore VSTHRD003

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Task.Run keeps the compile off the startup thread: the host must start serving its health
        // endpoints while the compiler runs.
        _run ??= Task.Run(WarmUp, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Shutdown must not wait for the compile: it cannot be interrupted, it runs on a pool thread that does
        // not keep the process alive, and the host's liveness contract is exit well inside its shutdown timeout.
        // Stop after Dispose is a no-op: a host disposing its container first then stopping the service is
        // normal for WebApplicationFactory, and it must not surface ObjectDisposedException from the disposed CTS.
        Task cancellation;
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            // Nothing can register on this private source, so the cancel runs to completion before the lock is
            // released and Dispose, which takes the same lock, never disposes under an in-flight cancel.
            cancellation = _stopping.CancelAsync();
        }

        await cancellation.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // A host that is disposed without StopAsync (failed start) must not leave the queued warm-up
        // resolving from a dead container and reporting that as a schema failure. The container may dispose twice,
        // and StopAsync may run before, after or beside this call.
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping.Cancel();
            _stopping.Dispose();
        }
    }

    private void WarmUp()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            lock (ColdLoad)
            {
                if (_stopping.IsCancellationRequested)
                {
                    _completion.SetCanceled();
                    return;
                }

                _ = _services.GetRequiredService<ContractSchemaSet>();
            }
        }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
        {
            _completion.SetCanceled();
            return;
        }
        catch (Exception exception)
        {
            LogFailed(exception);
            _completion.SetException(exception);
            return;
        }

        _completion.SetResult();
        var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogReady(elapsedMs);
    }

    [LoggerMessage(
        EventId = 1220,
        Level = LogLevel.Information,
        Message = "Contract schema set compiled at startup in {ElapsedMs} ms")]
    private partial void LogReady(long elapsedMs);

    [LoggerMessage(
        EventId = 1221,
        Level = LogLevel.Error,
        Message = "Contract schema set failed to build at startup; body-validating requests retry the load and fail while it does")]
    private partial void LogFailed(Exception exception);
}

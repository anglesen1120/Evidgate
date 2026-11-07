using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// Announces the quota gate switch this host resolved at start. The Api and the Worker bind
/// <c>EVIDGATE__USAGE__QUOTAGATE__ENABLED</c> on their own, and the ingest path runs in the Api while the report path
/// runs in the Worker, so a host switched off leaves the other half of the metering still refusing.
/// </summary>
internal sealed partial class QuotaGateStartupNotice : IHostedService
{
    private readonly IOptions<QuotaGateOptions> _options;
    private readonly ILogger<QuotaGateStartupNotice> _logger;

    /// <summary>Creates the notice over the switch this host bound and its own logger.</summary>
    /// <param name="options">The bound gate switch.</param>
    /// <param name="logger">The host's logger.</param>
    /// <remarks>
    /// Public because the container builds a hosted service from its public constructors; the type itself stays
    /// internal, so no other host can compose it.
    /// </remarks>
    public QuotaGateStartupNotice(
        IOptions<QuotaGateOptions> options,
        ILogger<QuotaGateStartupNotice> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Value.Enabled)
        {
            LogEnabled();
        }
        else
        {
            LogDisabled();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 2472,
        Level = LogLevel.Information,
        Message = "Quota gate is enabled on this host (Usage QuotaGate Enabled=true): metered work reserves "
            + "its counter before it commits")]
    private partial void LogEnabled();

    [LoggerMessage(
        EventId = 2473,
        Level = LogLevel.Warning,
        Message = "Quota gate is disabled on this host (Usage QuotaGate Enabled=false): this process "
            + "admits metered work without a counter. The Api runs the ingest path and the Worker the report path, and "
            + "each binds the switch alone, so both hosts must carry the same value")]
    private partial void LogDisabled();
}

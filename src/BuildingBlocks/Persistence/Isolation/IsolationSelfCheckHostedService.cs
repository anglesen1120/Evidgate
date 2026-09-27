using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.Persistence.Isolation;

/// <summary>
/// The boot-time isolation self-check of 02.6.1-T1.4 (area plan 02 §7, AC5): runs
/// once, before the host serves anything, in both entry points — the API and the
/// Worker, because the Worker claims tenant tasks and opens the same tenant scopes
/// (00 §5.4.2). A table the exception list does not excuse and that is not under
/// <c>FORCE ROW LEVEL SECURITY</c> with the required policy shape refuses the start:
/// <see cref="IsolationSelfCheckException"/> leaves the host with a non-zero exit
/// code and one named line, instead of a process that serves tenant traffic under a
/// database that stopped enforcing tenant isolation.
/// <para>
/// The single escape hatch is <see cref="IsolationSelfCheckOptions"/> — explicit,
/// and announced at Warning on every boot. A host that boots without a database
/// (the contract and shutdown suites) sets it; a deployment does not.
/// </para>
/// </summary>
public sealed partial class IsolationSelfCheckHostedService : IHostedService
{
    private readonly IsolationSelfCheck _check;
    private readonly IOptions<IsolationSelfCheckOptions> _options;
    private readonly ILogger<IsolationSelfCheckHostedService> _logger;

    /// <summary>Creates the service over the app-role check and the configured mode.</summary>
    public IsolationSelfCheckHostedService(
        IsolationSelfCheck check,
        IOptions<IsolationSelfCheckOptions> options,
        ILogger<IsolationSelfCheckHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _check = check;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Value.SelfCheck == IsolationSelfCheckMode.Disabled)
        {
            LogDisabled();
            return;
        }

        var report = await _check.RunAsync(cancellationToken).ConfigureAwait(false);

        if (!report.Passed)
        {
            var refusal = new IsolationSelfCheckException(report);
            LogRefused(refusal.Message);
            throw refusal;
        }

        LogPassed(report.CheckedTables.Count, report.ExemptTables);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1210,
        Level = LogLevel.Warning,
        Message = "Isolation self-check is DISABLED by configuration (EVIDGATE__ISOLATION__SELFCHECK=Disabled): "
            + "this process does not verify that every non-exempt table runs under FORCE ROW LEVEL SECURITY "
            + "(02.6.1-T1.4 AC5). Never set this where tenant traffic is served.")]
    private partial void LogDisabled();

    [LoggerMessage(
        EventId = 1211,
        Level = LogLevel.Information,
        Message = "Isolation self-check passed: {CheckedTables} table(s) under forced row level security, "
            + "{ExemptTables} mechanism table(s) exempt")]
    private partial void LogPassed(int checkedTables, int exemptTables);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Error, Message = "{Refusal}")]
    private partial void LogRefused(string refusal);
}

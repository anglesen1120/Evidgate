using Dapper;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The operator route's call of <c>evidgate.healing_switch_set_system</c> on a connection of its own, outside any
/// tenant transaction: the platform row carries no <c>org_id</c>, so the tenant write policy can never reach it.
/// This file is the only place in <c>src/</c> that names the function (the architecture suite pins that).
/// <para>
/// The API role holds no EXECUTE on that definer: it is granted to the platform operator login alone, which records
/// every change in the platform operator audit table. A call from here is refused by the database (42501) and
/// answered as a 403-class failure — the platform switch is never writable from an API role.
/// </para>
/// </summary>
/// <param name="dataSource">The non-tenant data source (the operator ingress pattern).</param>
/// <param name="apply">The V1 clamp.</param>
/// <param name="logger">The structured event that records the attempt and the refusal.</param>
internal sealed partial class HealingSystemSwitchWriter(
    NpgsqlDataSource dataSource,
    IHealingApplyPolicy apply,
    ILogger<HealingSystemSwitchWriter> logger)
{
    /// <summary>
    /// The definer call (migration 0052). The casts carry the <c>NULL</c>s Npgsql cannot type; the parameters are
    /// named for the function's own argument names so a rename shows up here.
    /// </summary>
    private const string SetSystem = """
        select mode, reason, set_by, at, version
          from evidgate.healing_switch_set_system(@mode::text, @reason::text, @set_by::uuid)
        """;

    /// <summary>
    /// Judges the pair with the shared V1 gate, then calls the definer — which the API role may not execute, so the
    /// answer is a 403-class failure naming neither the connection nor the reason text.
    /// </summary>
    /// <param name="mode">The requested mode token.</param>
    /// <param name="reason">The optional reason, 1–2000 Unicode code points.</param>
    /// <param name="setBy">The authenticated operator principal's user id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<HealingSwitchView>> SetAsync(
        string? mode,
        string? reason,
        Guid setBy,
        CancellationToken cancellationToken)
    {
        if (HealingModeGate.Check(mode, reason, apply.IsApplyEnabled) is { } invalid)
        {
            return Result.Failure<HealingSwitchView>(invalid);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        SystemSwitchRow? row;
        try
        {
            row = await connection.QuerySingleOrDefaultAsync<SystemSwitchRow>(
                new CommandDefinition(
                    SetSystem,
                    new { mode, reason, set_by = setBy },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // The definer belongs to the platform operator login, so this role cannot write the platform row: the
            // refusal is a deployment fact, not a server fault, and must never surface as a 500.
            LogSystemSwitchNotWritable(mode);
            return Result.Failure<HealingSwitchView>(Error.Forbidden(
                "the platform healing switch is not writable from the API role"));
        }

        if (row is null)
        {
            throw new InvalidOperationException("evidgate.healing_switch_set_system returned no row");
        }

        // The record this path keeps when the call is allowed: who set the platform row, to what, and the version
        // the trigger bumped. The reason text stays out of the log (00 §5.7 S3).
        LogSystemSwitchSet(row.Mode, setBy, row.Version);

        return Result.Success(new HealingSwitchView(
            HealingSwitchScopes.System,
            ProjectId: null,
            row.Mode,
            row.Reason,
            row.SetBy,
            row.At,
            row.Version));
    }

    /// <summary>The structured record of one refused platform-wide switch write (the attempted mode only).</summary>
    /// <param name="mode">The mode the caller asked for.</param>
    [LoggerMessage(
        EventId = 8604,
        Level = LogLevel.Warning,
        Message = "Platform healing switch write refused: the API role holds no EXECUTE on the definer (mode={Mode})")]
    private partial void LogSystemSwitchNotWritable(string? mode);

    /// <summary>The structured record of one platform-wide switch write (mode, operator and version only).</summary>
    /// <param name="mode">The mode now stored.</param>
    /// <param name="setBy">The operator principal who set it.</param>
    /// <param name="version">The row version after the write.</param>
    [LoggerMessage(
        EventId = 8603,
        Level = LogLevel.Information,
        Message = "Platform healing switch set: mode={Mode} set_by={SetBy} version={Version}")]
    private partial void LogSystemSwitchSet(string mode, Guid setBy, int version);

    /// <summary>One row of the definer's <c>RETURNS TABLE</c>, in its declared column order.</summary>
    /// <param name="Mode">The stored mode.</param>
    /// <param name="Reason">The stored reason, or null.</param>
    /// <param name="SetBy">The operator who set it.</param>
    /// <param name="At">When it was set.</param>
    /// <param name="Version">The row's version after the write.</param>
    internal sealed record SystemSwitchRow(string Mode, string? Reason, Guid SetBy, DateTimeOffset At, int Version);
}

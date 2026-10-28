using Dapper;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The operator's write path to the platform-wide <c>system</c> row (08.6.3 §3/§5, ruling T72): it calls
/// <c>evidgate.healing_switch_set_system</c> on a connection of its own, outside any tenant transaction, because the
/// row carries no <c>org_id</c> and the tenant write policy can never reach it. This file is the only place in
/// <c>src/</c> that names the function (the architecture suite pins that), and the definer itself refuses to run
/// inside a tenant transaction.
/// <para>
/// The V1 clamp applies here exactly as on the tenant route (08.6.3 AC7): <c>enabled</c> is 422
/// <c>mode_not_available</c> while <c>Healing:Apply</c> is off, whatever the scope, so the operator cannot switch on
/// healing the deployment was not configured to run.
/// </para>
/// <para>
/// <b>Why there is no audit row and no outbox event here.</b> Both of those chains are per-org: <c>audit_log</c> and
/// <c>outbox</c> carry a non-null <c>org_id</c> and their writers run inside the caller's tenant transaction, and this
/// row deliberately has no org (it is the row every org inherits). There is therefore no chain a row could join
/// without inventing an owning tenant. The change is recorded instead by a structured log event (mode, operator,
/// resulting version — never the reason text, which may name a customer) and by the row itself
/// (<c>set_by</c>/<c>at</c>/<c>version</c>); the platform-wide audit chain for operator actions is 11.5.1-T5's and the
/// authenticated operator principal is 02.8.1's.
/// </para>
/// </summary>
/// <param name="dataSource">The non-tenant data source (the operator ingress pattern).</param>
/// <param name="apply">The V1 clamp.</param>
/// <param name="logger">The structured event that stands in for an org-scoped audit row on this path.</param>
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

    /// <summary>Upserts the single system row and answers what the database now stores.</summary>
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

        var row = await connection.QuerySingleOrDefaultAsync<SystemSwitchRow>(
            new CommandDefinition(
                SetSystem,
                new { mode, reason, set_by = setBy },
                cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("evidgate.healing_switch_set_system returned no row");

        // The record this path has instead of an org-scoped audit row: who set the platform row, to what, and the
        // version the trigger bumped. The reason text stays out of the log (00 §5.7 S3).
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

using Dapper;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Npgsql;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The operator's write path to the platform-wide <c>system</c> row (08.6.3 §3/§5, ruling T72): it calls
/// <c>evidgate.healing_switch_set_system</c> on a connection of its own, outside any tenant transaction, because the
/// row carries no <c>org_id</c> and the tenant write policy can never reach it. It is the one place outside
/// <c>migrations/</c> that names the function (the architecture suite pins that).
/// <para>
/// The V1 clamp applies here exactly as on the tenant route (08.6.3 AC7): <c>enabled</c> is 422
/// <c>mode_not_available</c> while <c>Healing:Apply</c> is off, whatever the scope, so the operator cannot switch on
/// healing the deployment was not configured to run.
/// </para>
/// </summary>
/// <param name="dataSource">The non-tenant data source (the operator ingress pattern).</param>
/// <param name="apply">The V1 clamp.</param>
internal sealed class HealingSystemSwitchWriter(NpgsqlDataSource dataSource, IHealingApplyPolicy apply)
{
    /// <summary>Upserts the single system row and answers what the database now stores.</summary>
    /// <param name="mode">The requested mode token.</param>
    /// <param name="reason">The optional reason, 1–2000 characters.</param>
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
                HealingSwitchSql.SetSystem,
                new { mode, reason, set_by = setBy },
                cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("evidgate.healing_switch_set_system returned no row");

        return Result.Success(new HealingSwitchView(
            HealingSwitchScopes.System,
            ProjectId: null,
            row.Mode,
            row.Reason,
            row.SetBy,
            row.At,
            row.Version));
    }

    /// <summary>One row of the definer's <c>RETURNS TABLE</c>, in its declared column order.</summary>
    /// <param name="Mode">The stored mode.</param>
    /// <param name="Reason">The stored reason, or null.</param>
    /// <param name="SetBy">The operator who set it.</param>
    /// <param name="At">When it was set.</param>
    /// <param name="Version">The row's version after the write.</param>
    internal sealed record SystemSwitchRow(string Mode, string? Reason, Guid SetBy, DateTimeOffset At, int Version);
}

using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The Dapper read of <see cref="IRunEventScope"/> (04.1.1-T1, rulings T90/T91): 0031's <c>run</c>/<c>planned_execution</c>
/// and 0005's <c>project</c>. One statement over the ambient tenant scope, so it joins the caller's transaction and
/// RLS scopes every row (00 §5.5 C3/C5), exactly like <see cref="RunPlanReader"/>; the SQL passes the org and run
/// ids explicitly as well as relying on RLS, exactly like <see cref="OrchestratorSql"/>. Read-only by construction —
/// it never writes.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
internal sealed partial class RunEventScopeReader(ITenantDb db, ITenantScopeAccessor scopes) : IRunEventScope
{
    /// <summary>The largest key collection one call accepts: the events route caps a batch at 500 events (00 §9.3).</summary>
    internal const int MaxKeys = 500;

    /// <summary>
    /// The one statement of <see cref="ReadAsync"/> (04.1.1-T1, rulings T90/T91). The run row is locked
    /// <c>FOR SHARE OF r</c> — only <c>run</c>, not the joined <c>project</c> — so it serialises against the
    /// <c>FOR NO KEY UPDATE</c> the transition takes (a cancel, a completion, the first event; T12/T81) and a
    /// committed state change can never be missed;
    /// the same statement carries the project's state and the planned-execution membership probe. The probe is one
    /// <c>execution_key = ANY(@keys)</c> on the unique index <c>planned_execution_run_execution_key</c>
    /// (00 §5.7 P3, T90): the run's whole planned set is never loaded, and an empty key set yields the empty array.
    /// <c>org_id</c> and <c>run_id</c> are passed explicitly as well as relying on RLS. An invisible run answers no row.
    /// <para>
    /// Privilege: PostgreSQL requires <c>UPDATE</c> on at least one column of every table a locking clause names, so
    /// this <c>FOR SHARE</c> needs a column grant on <c>run</c> — 0031 revokes its table-level <c>UPDATE</c>. 0034
    /// grants <c>update (updated_at) on public.run</c> to the app role for exactly this: the shared
    /// <c>set_updated_at_and_version</c> trigger owns that column, so the grant unlocks the row lock without any
    /// write power over <c>state</c>, which stays 05.3.1's guarded <c>grant update (state)</c> (0031 line 53).
    /// Without such a grant the read is denied <c>42501</c> (the same reason quarantine locks <c>project</c>, not
    /// <c>policy_revision</c>).
    /// </para>
    /// </summary>
    internal const string ReadScope = """
        select r.project_id as project_id,
               p.status as project_state,
               r.state as run_state,
               now() <= r.collection_deadline_at + make_interval(mins => r.late_window_minutes) as late_window_open,
               (select coalesce(array_agg(pe.execution_key order by pe.execution_key), '{}'::text[])
                  from public.planned_execution pe
                 where pe.org_id = @org_id and pe.run_id = @run_id
                   and pe.execution_key = any(@keys)) as planned_keys
          from public.run r
          join public.project p on p.org_id = r.org_id and p.id = r.project_id
         where r.org_id = @org_id and r.id = @run_id
         for share of r
        """;

    /// <inheritdoc />
    public async ValueTask<RunEventScope?> ReadAsync(
        Guid runId,
        IReadOnlyCollection<string> executionKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionKeys);

        // Argument problems are the caller's mistake and are reported before the scope guard, so a batch with a bad
        // key never depends on whether a scope happens to be open.
        var keys = DistinctValidKeys(executionKeys);

        // The tenant-scope guard runs before any statement (00 §5.5 C3): with no open scope this throws and nothing is sent.
        var scope = scopes.Require();

        var row = await db.QuerySingleOrDefaultAsync<RunEventScopeRow>(
            ReadScope,
            new { org_id = scope.OrgId, run_id = runId, keys = new TextArrayParameter(keys) },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? null
            : new RunEventScope(
                row.ProjectId,
                row.ProjectState,
                row.RunState,
                row.PlannedKeys.ToHashSet(StringComparer.Ordinal),
                row.LateWindowOpen);
    }

    /// <summary>
    /// The batch's distinct keys, guarded against SQL abuse (04.1.1-T1): a key that is not 64 lowercase hex cannot
    /// match a planned execution (0031's <c>planned_execution_execution_key_check</c>), so it is refused rather than
    /// sent, and the caller's own de-duplication is repeated here so the array holds no repeat. An empty collection
    /// is allowed and answers the empty set.
    /// </summary>
    private static string[] DistinctValidKeys(IReadOnlyCollection<string> executionKeys)
    {
        var keys = new List<string>(executionKeys.Count);
        var seen = new HashSet<string>(executionKeys.Count, StringComparer.Ordinal);

        foreach (var key in executionKeys)
        {
            if (key is null || !ExecutionKeyShape().IsMatch(key))
            {
                throw new ArgumentException(
                    $"every execution key is 64 lowercase hex characters (00 §7), not '{key}'.", nameof(executionKeys));
            }

            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        if (keys.Count > MaxKeys)
        {
            throw new ArgumentException(
                $"an event batch holds at most {MaxKeys} distinct execution keys, not {keys.Count}.", nameof(executionKeys));
        }

        return [.. keys];
    }

    /// <summary>The 00 §7 execution-key shape: sha256 hex, lowercase (0031's <c>planned_execution_execution_key_check</c>).</summary>
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionKeyShape();

    /// <summary>One row as <see cref="ReadScope"/> returns it (snake_case → PascalCase, Dapper's underscore map).</summary>
    private sealed class RunEventScopeRow
    {
        public Guid ProjectId { get; set; }

        public string ProjectState { get; set; } = string.Empty;

        public string RunState { get; set; } = string.Empty;

        public bool LateWindowOpen { get; set; }

        public string[] PlannedKeys { get; set; } = [];
    }

    /// <summary>
    /// The <c>text[]</c> parameter of <see cref="ReadScope"/>, typed explicitly. Dapper cannot send a
    /// <c>string[]</c> here: the tenant read path registers a read-only <c>string[]</c> type handler
    /// (<c>StringArrayTypeHandler</c>, M1W3-02 — writes are EF Core's job), so Dapper's type-handler registry refuses
    /// to write the array. Dapper's custom-parameter hook runs before that registry. This is the module-local twin
    /// of <c>Evidgate.BuildingBlocks.Persistence.Tasks.TextArrayParameter</c>, which is internal to its own assembly
    /// and so is not visible to a module (00 §5.5: a module reaches another only through its <c>.Contracts</c>).
    /// </summary>
    private sealed class TextArrayParameter(string[] values) : SqlMapper.ICustomQueryParameter
    {
        /// <inheritdoc />
        public void AddParameter(IDbCommand command, string name)
        {
            ArgumentNullException.ThrowIfNull(command);

            var parameter = (NpgsqlParameter)command.CreateParameter();
            parameter.ParameterName = name;
            parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            parameter.Value = values;

            command.Parameters.Add(parameter);
        }
    }
}

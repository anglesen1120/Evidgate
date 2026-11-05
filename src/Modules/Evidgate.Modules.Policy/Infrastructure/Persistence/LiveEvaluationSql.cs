namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The one statement behind <see cref="Contracts.ILiveEvaluationReader"/>: a run's stored <c>live</c> verdict, matched on
/// the ambient org on top of RLS. It reads no shadow row — a shadow verdict is a second opinion about the same evidence,
/// not the verdict the run published.
/// </summary>
internal static class LiveEvaluationSql
{
    /// <summary>True when one live evaluation row exists for the run in this org.</summary>
    internal const string HasLive = """
        select exists (select 1
                         from public.evaluation e
                        where e.org_id = @org_id and e.run_id = @run_id and e.mode = 'live')
        """;
}

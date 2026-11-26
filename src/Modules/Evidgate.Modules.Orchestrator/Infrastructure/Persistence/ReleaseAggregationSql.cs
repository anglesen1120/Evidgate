namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>The statements behind the release aggregation reader; all read-only and parameterised.</summary>
internal static class ReleaseAggregationSql
{
    /// <summary>The release's project; RLS and the org predicate make another tenant's id the same null.</summary>
    internal const string ProjectOfRelease = """
        select r.project_id
          from public.release r
         where r.id = @release_id and r.org_id = @org_id
        """;

    /// <summary>
    /// The bindings in insertion order with the bound run's purpose and recorded digest for the bound key. One
    /// statement for every component; one extra row is read so a release over the ceiling is detected, not truncated.
    /// </summary>
    internal const string Bindings = """
        select rr.run_id as "RunId",
               rr.component_key as "ComponentKey",
               ru.purpose as "Purpose",
               ru.components ->> rr.component_key as "RunDigest"
          from public.release_run rr
          join public.run ru
            on ru.org_id = rr.org_id and ru.id = rr.run_id
         where rr.org_id = @org_id and rr.release_id = @release_id
         order by rr.created_at, rr.id
         limit @limit
        """;

    /// <summary>The distinct releases one run is bound to, served by the <c>(org_id, run_id)</c> binding index.</summary>
    internal const string ReleasesOfRun = """
        select rr.release_id
          from public.release_run rr
         where rr.org_id = @org_id and rr.run_id = @run_id
         group by rr.release_id
         order by min(rr.created_at), rr.release_id
         limit @limit
        """;
}

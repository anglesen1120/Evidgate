namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The one read of a run's manifest-declared producer kind. The hint is a value the statement returns, never text it
/// interpolates, so a hostile hint stays data, and every declared hint reaches the caller whatever its shape: which
/// hints are kinds is <c>raw_report</c>'s column shape, decided once by the reader's own predicate. A value that is not
/// a string is no hint at all, and a <c>shards</c> value that is not an array (a hand-seeded or restored manifest)
/// declares none — the caller reports that as unknown instead of the statement failing.
/// </summary>
internal static class RawReportProducerKindSql
{
    /// <summary>
    /// At most two distinct hints reach the caller, which is all it needs to tell one kind from several. The limit
    /// bounds the rows the caller materialises, not the statement's work: the distinct still visits every shard, so the
    /// read costs what the manifest's shard count costs whatever this bound is.
    /// </summary>
    internal const string ProducerKinds = """
        select distinct shards.value ->> 'producer_hint' as hint
        from public.run r
        join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
        cross join lateral jsonb_array_elements(
          case when jsonb_typeof(m.shard_plan -> 'shards') = 'array'
               then m.shard_plan -> 'shards'
               else '[]'::jsonb
          end) as shards(value)
        where r.id = @run_id
          and jsonb_typeof(shards.value -> 'producer_hint') = 'string'
        limit 2
        """;
}

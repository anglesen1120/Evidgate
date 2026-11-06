namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The one read of a run's manifest-declared producer kind. The hint is a value the statement returns, never text it
/// interpolates, so a hostile hint stays data; the length bound re-checks the contract's 1..64 characters because a
/// hand-seeded or restored manifest never passed the upload verifier.
/// </summary>
internal static class RawReportProducerKindSql
{
    /// <summary>
    /// At most two distinct hints, which is all the caller needs to tell one kind from several; the whole distinct set
    /// would make the read's cost follow the manifest's shard count instead of its answer.
    /// </summary>
    internal const string ProducerKinds = """
        select distinct shards.value ->> 'producer_hint' as hint
        from public.run r
        join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
        cross join jsonb_array_elements(m.shard_plan -> 'shards') as shards(value)
        where r.id = @run_id
          and jsonb_typeof(shards.value -> 'producer_hint') = 'string'
          and char_length(shards.value ->> 'producer_hint') between 1 and 64
        limit 2
        """;
}

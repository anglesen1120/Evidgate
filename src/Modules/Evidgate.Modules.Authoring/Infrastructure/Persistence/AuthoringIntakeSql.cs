namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements of the intake routes. Every one names <c>org_id</c> and <c>project_id</c>, so the primary-key and
/// <c>(org_id, project_id, created_at desc, id desc)</c> indexes serve them and a sibling project's row is never read.
/// </summary>
internal static class AuthoringIntakeSql
{
    /// <summary>A live feature node of the project, the target of a text or document intake.</summary>
    internal const string LiveNode = """
        select 1
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.id = @id
           and n.deleted_at is null
        """;

    /// <summary>The intake that already holds an artifact, for the duplicate answer before the unique key fires.</summary>
    internal const string ByArtifact = """
        select i.id
          from public.authoring_intake i
         where i.org_id = @org_id
           and i.artifact_id = @artifact_id
        """;

    /// <summary>Inserts one intake in <c>received</c>; the version is the row's own.</summary>
    internal const string Insert = """
        insert into public.authoring_intake
            (id, org_id, project_id, feature_node_id, kind, artifact_id, input_text, media_type, declared_bytes, caps, created_by)
        values
            (@id, @org_id, @project_id, @feature_node_id, @kind, @artifact_id, @input_text, @media_type, @declared_bytes,
             cast(@caps as jsonb), @created_by)
        returning version
        """;

    /// <summary>The inline completion of a text intake: the one terminal write of a request.</summary>
    internal const string CompleteText = """
        update public.authoring_intake
           set state = 'ready', extracted_text = @text, extracted_text_hash = @hash
         where org_id = @org_id
           and id = @id
           and state = 'received'
        returning version
        """;

    /// <summary>The completion of an image intake, which stores a reference and has nothing to extract.</summary>
    internal const string CompleteImage = """
        update public.authoring_intake
           set state = 'ready'
         where org_id = @org_id
           and id = @id
           and state = 'received'
        returning version
        """;

    /// <summary>One intake for the status read; the text itself is never selected, only its code-point count.</summary>
    internal const string Status = """
        select i.id,
               i.kind,
               i.state,
               i.failure_code,
               i.extracted_text_hash,
               case when i.extracted_text is null then null else char_length(i.extracted_text) end as extracted_char_count,
               i.artifact_id,
               i.caps::text as caps,
               i.version,
               i.created_at
          from public.authoring_intake i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.id = @id
        """;

    /// <summary>
    /// The state, the code-point count and the selected code points of one intake's extracted text. The offsets are
    /// clamped inside the text in <c>bigint</c> before they reach <c>substr</c>, so an out-of-range request reads an
    /// empty slice instead of failing, and only the selection is ever returned, never the whole text.
    /// </summary>
    internal const string Selection = """
        select s.state,
               s.char_count,
               case when s.text is null then null
                    else substr(s.text, cast(s.from_at as integer) + 1, cast(greatest(s.to_at - s.from_at, 0) as integer))
               end as selection
          from (select i.state,
                       i.extracted_text as text,
                       char_length(i.extracted_text) as char_count,
                       least(greatest(cast(@start_at as bigint), 0), char_length(i.extracted_text)) as from_at,
                       least(coalesce(cast(@end_at as bigint), char_length(i.extracted_text)), char_length(i.extracted_text)) as to_at
                  from public.authoring_intake i
                 where i.org_id = @org_id
                   and i.project_id = @project_id
                   and i.id = @id) s
        """;

    /// <summary>
    /// How many unfinished intakes of the tenant were created before one, stopping at <c>@bound</c>: the queue position the
    /// <c>eta_class</c> is judged from, bounded so a deep queue costs no more than a shallow one. The state list and the
    /// order match <c>authoring_intake_pending_created_idx</c>, so the count walks that partial index and never the
    /// finished rows.
    /// </summary>
    internal const string QueueAhead = """
        select count(*)::int
          from (select 1
                  from public.authoring_intake i
                 where i.org_id = @org_id
                   and i.state in ('received', 'extracting')
                   and (i.created_at, i.id) < (@created_at, @id)
                 order by i.created_at, i.id
                 limit @bound) ahead
        """;
}

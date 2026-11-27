namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>The statements on <c>test_report</c> and on the outcome of its queue job; every one names the organization and the project.</summary>
internal static class TestReportSql
{
    /// <summary>
    /// Publishes a snapshot. The job key makes a redelivered job a no-op, so a job produces at most one report.
    /// </summary>
    internal const string Insert = """
        insert into public.test_report
          (id, org_id, project_id, job_id, scope_kind, selected_ids, sections, language, schema_version,
           pinned_versions, payload, content_hash, created_by)
        values
          (@id, @org_id, @project_id, @job_id, @scope_kind, @selected_ids::jsonb, @sections::jsonb, @language,
           @schema_version, @pinned_versions::jsonb, @payload::jsonb, @content_hash, @created_by)
        on conflict on constraint test_report_job_key do nothing
        """;

    /// <summary>The report a job published, if it did.</summary>
    internal const string ByJob = """
        select id from public.test_report
         where org_id = @org_id and project_id = @project_id and job_id = @job_id
        """;

    /// <summary>One newest-first page of summaries; the payload is never read here.</summary>
    internal const string List = """
        select id, job_id, scope_kind, language, content_hash, created_at
          from public.test_report
         where org_id = @org_id and project_id = @project_id
           and (@after_created_at::timestamptz is null
                or (created_at, id) < (@after_created_at::timestamptz, @after_id::uuid))
         order by created_at desc, id desc
         limit @limit
        """;

    /// <summary>One report with its snapshot.</summary>
    internal const string Get = """
        select id, job_id, scope_kind, language, content_hash, created_at, schema_version,
               selected_ids::text as selected_ids, sections::text as sections, payload::text as payload
          from public.test_report
         where org_id = @org_id and project_id = @project_id and id = @id
        """;

    /// <summary>
    /// The queue state of a job, through the tenant-filtered view of outbox and task: the app role may only insert into
    /// the task table itself.
    /// </summary>
    internal const string JobTask = """
        select dispatched_at is not null as dispatched, task_state, last_error
          from public.outbox_task_in_scope
         where org_id = @org_id and idempotency_key = @idempotency_key
        """;
}

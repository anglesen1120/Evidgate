using Npgsql;

namespace Isolation.Tests.Api;

internal static class ReportReadSeed
{
    // A report is published by a worker job; the isolation host runs none, so the owner inserts the published row
    // the history, detail and job-outcome reads of the foreign probes must not reveal.
    internal static async Task<(Guid ReportId, Guid JobId)> SeedAsync(
        string connectionString, Guid org, Guid project, Guid requirement, CancellationToken token)
    {
        var reportId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = """
            insert into public.test_report
                (id, org_id, project_id, job_id, scope_kind, selected_ids, sections, language,
                 schema_version, pinned_versions, payload, content_hash)
            values (@id, @org, @project, @job, 'requirements', jsonb_build_array(@requirement::text),
                '["test_plan"]'::jsonb, 'en', 1, '{}'::jsonb, '{}'::jsonb, @hash)
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", reportId);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("requirement", requirement);
        command.Parameters.AddWithValue("hash", "sha256:" + new string('a', 64));
        await command.ExecuteNonQueryAsync(token);
        return (reportId, jobId);
    }
}

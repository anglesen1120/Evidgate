using Npgsql;

namespace Isolation.Tests.Api;

internal static class IncidentReadSeed
{
    // Use the second run so first-run attempt and event probes retain their empty control.
    internal static async Task<IReadOnlyList<Guid>> SeedAsync(string connectionString, Guid org, Guid project, Guid run, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        const string sql = """
            insert into public.case_attempt (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select gen_random_uuid(), p.org_id, p.run_id, p.execution_key, rev.case_id, 1, 'failed', 'failed'
              from public.planned_execution p join public.test_revision rev on (rev.org_id, rev.id) = (p.org_id, p.case_revision_id)
             where p.org_id = @org and p.run_id = @run;
            insert into public.incident (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id, execution_key, engine)
            select gen_random_uuid(), a.org_id, @project, a.run_id, a.id, a.case_id, p.case_revision_id, a.execution_key, 'playwright'
              from public.case_attempt a join public.planned_execution p using (org_id, run_id, execution_key)
             where a.org_id = @org and a.run_id = @run;
            update public.incident set state = 'triaged' where id =
                (select id from public.incident where org_id = @org and run_id = @run order by id limit 1);
            select id from public.incident where org_id = @org and run_id = @run order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("run", run);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync(token);
        return ids;
    }
}

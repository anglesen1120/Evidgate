using Npgsql;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The summary's half of the usage-read world: the route of one org's summary and the callers a summary test sends it
/// as, plus the counter seed the statement answers from. Kept apart so one file stays inside the size the repository
/// allows a file.
/// </summary>
internal sealed partial class UsageReadsWorld
{
    /// <summary>The url of the tenant usage summary of this world's org.</summary>
    /// <param name="project">The project filter, or null for the org-wide answer.</param>
    internal string SummaryUrl(Guid? project = null) =>
        $"/v1/orgs/{Org:D}/usage/summary" + (project is { } id ? $"?project_id={id:D}" : string.Empty);

    /// <summary>Sends a GET as the founder of this world's org, who holds <c>tenant_admin</c>.</summary>
    /// <param name="url">The route.</param>
    internal Task<ApiAnswer> GetAsMemberAsync(string url) =>
        Client.SendAsync(HttpMethod.Get, url, TenancyApiFactory.Resolved(user: User, orgHeader: Org));

    /// <summary>Sends a GET as a service credential naming this org and project with the given permissions.</summary>
    /// <param name="url">The route.</param>
    /// <param name="permissions">The permissions the credential asks for.</param>
    internal Task<ApiAnswer> GetAsServiceAsync(string url, params string[] permissions) =>
        Client.SendAsync(
            HttpMethod.Get,
            url,
            TenancyApiFactory.Resolved(serviceOrg: Org, serviceProject: Project, servicePermissions: permissions));

    /// <summary>Inserts the given quota counters, one parameterised statement each.</summary>
    /// <param name="rows">The counters to insert.</param>
    internal async Task SeedCounterAsync(params CounterSeed[] rows)
    {
        await using var connection = await OpenOwnerAsync();

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into public.quota_counter
                    (id, org_id, project_id, period, metric, used, quota_limit, warn_ratio, warned_at, blocked_at)
                values
                    (@id, @org_id, @project_id, @period, @metric, @used, @quota_limit, @warn_ratio, @warned_at,
                     @blocked_at)
                """;
            command.Parameters.Add(new NpgsqlParameter("id", row.Id));
            command.Parameters.Add(new NpgsqlParameter("org_id", row.Org ?? Org));
            command.Parameters.Add(new NpgsqlParameter("project_id", (object?)row.ProjectId ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("period", row.Period));
            command.Parameters.Add(new NpgsqlParameter("metric", row.Metric));
            command.Parameters.Add(new NpgsqlParameter("used", row.Used));
            command.Parameters.Add(new NpgsqlParameter("quota_limit", row.Limit));
            command.Parameters.Add(new NpgsqlParameter("warn_ratio", row.WarnRatio));
            command.Parameters.Add(new NpgsqlParameter("warned_at", (object?)row.WarnedAt ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("blocked_at", (object?)row.BlockedAt ?? DBNull.Value));

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}

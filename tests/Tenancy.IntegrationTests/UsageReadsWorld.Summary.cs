using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>Sends a GET as a member of this world's org who holds only the given built-in role.</summary>
    /// <param name="fixture">The fixture that owns the database.</param>
    /// <param name="roleCode">The built-in role code granted to the new member.</param>
    /// <param name="url">The route.</param>
    internal async Task<ApiAnswer> GetAsMemberWithRoleAsync(PostgresTenancyFixture fixture, string roleCode, string url)
    {
        var member = await RegistryApi.AddMemberAsync(
            fixture, Client, Database.Name, Org, TenancyApiFactory.Resolved(user: User, orgHeader: Org), roleCode);
        return await Client.SendAsync(HttpMethod.Get, url, TenancyApiFactory.Resolved(user: member, orgHeader: Org));
    }

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

    /// <summary>
    /// The database's own month and instant, read as the owner: what the summary's tenant transaction will name, and
    /// the instant its retry hint is measured from.
    /// </summary>
    internal async Task<(string Period, DateTimeOffset Instant)> DatabaseClockAsync()
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select to_char(now() at time zone 'UTC','YYYY-MM'), (extract(epoch from now()) * 1000)::bigint";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);

        return (reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
    }

    /// <summary>A second host over this world's database whose own clock reports <paramref name="instant"/>.</summary>
    /// <param name="instant">The instant the host's <see cref="TimeProvider"/> answers.</param>
    internal TenancyApiFactory HostAt(DateTimeOffset instant) =>
        TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(Database),
            services: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(instant));
            });
}

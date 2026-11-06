using Npgsql;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The cost side of the billing operator's world: the per-tenant cost route of this org, and the healing gateway's
/// attempt rows the token line falls back to.
/// </summary>
internal sealed partial class UsageReadsWorld
{
    /// <summary>The url of the per-tenant cost of this world's org.</summary>
    /// <param name="query">The query string, without a leading <c>?</c>.</param>
    internal string CostUrl(string query = "") =>
        $"/internal/operator/orgs/{Org:D}/cost{(query.Length == 0 ? string.Empty : "?" + query)}";

    /// <summary>The url of one org's per-tenant cost.</summary>
    /// <param name="org">The org the route names.</param>
    /// <param name="query">The query string, without a leading <c>?</c>.</param>
    internal static string CostUrlFor(Guid org, string query = "") =>
        $"/internal/operator/orgs/{org:D}/cost{(query.Length == 0 ? string.Empty : "?" + query)}";

    /// <summary>
    /// Inserts the given healing-gateway attempts. Each row is an <c>authoring_generation</c>, the one purpose that
    /// carries neither an incident nor a candidate, so a seed needs nothing beyond the call itself.
    /// </summary>
    /// <param name="rows">The attempts to insert.</param>
    internal async Task SeedHealingCallsAsync(params HealingCallSeed[] rows)
    {
        await using var connection = await OpenOwnerAsync();

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into public.healing_llm_call
                    (id, org_id, project_id, purpose, model, prompt_version, tokens, cost, status, attempt,
                     created_at)
                values
                    (@id, @org_id, @project_id, 'authoring_generation', 'test-model', 'test-v1', @tokens, 0,
                     @status, 1, @created_at)
                """;
            command.Parameters.Add(new NpgsqlParameter("id", row.Id));
            command.Parameters.Add(new NpgsqlParameter("org_id", row.Org ?? Org));
            command.Parameters.Add(new NpgsqlParameter("project_id", row.ProjectId ?? Project));
            command.Parameters.Add(new NpgsqlParameter("tokens", row.Tokens));
            command.Parameters.Add(new NpgsqlParameter("status", row.Status));
            command.Parameters.Add(new NpgsqlParameter("created_at", row.CreatedAt));

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>One healing-gateway attempt to seed: the tokens it burned, its outcome and when it happened.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Tokens">The tokens the provider reported for the attempt.</param>
/// <param name="Status">One of migration 0054's five statuses.</param>
/// <param name="CreatedAt">When the attempt was recorded.</param>
/// <param name="ProjectId">The project the call is attributed to, or null for this world's project.</param>
/// <param name="Org">The org the call belongs to, or null for this world's.</param>
internal sealed record HealingCallSeed(
    Guid Id,
    int Tokens,
    string Status,
    DateTimeOffset CreatedAt,
    Guid? ProjectId = null,
    Guid? Org = null);

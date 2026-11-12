using Evidgate.Modules.Healing.Application;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The daily roll of the healing LLM budget against a real PostgreSQL: a new day gets its own row with the earlier caps
/// and zero usage, the roll is idempotent, only the sweep role may run it, and a hold taken on the old day settles on
/// that day's row instead of being erased or double-released.
/// </summary>
[Trait("Category", "Docker")]
public sealed class HealingLlmBudgetResetTests
{
    private const string Today = "(now() at time zone 'utc')::date";

    private readonly PostgresTenancyFixture _fixture;

    public HealingLlmBudgetResetTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Roll_OpensTodaysRowWithEarlierCapsAndZeroUsage_AndIsIdempotent()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedProjectAsync(dedicated.Name);
        await SeedRowAsync(dedicated.Name, org, project, $"{Today} - 1", 2.0m, 9000, 1.5m, 800);

        (await RollAsWorkerAsync(dedicated.Name)).ShouldBe(1);

        var today = await ReadRowAsync(dedicated.Name, project, $"{Today}");
        today.ShouldBe((2.0m, 9000, 0m, 0));

        // The earlier day is history: the roll never touches it.
        (await ReadRowAsync(dedicated.Name, project, $"{Today} - 1")).ShouldBe((2.0m, 9000, 1.5m, 800));

        // A rerun of the same fire opens nothing and zeroes nothing.
        await SpendTodayAsync(dedicated.Name, project, 0.2m, 30);
        (await RollAsWorkerAsync(dedicated.Name)).ShouldBe(0);
        (await ReadRowAsync(dedicated.Name, project, $"{Today}")).ShouldBe((2.0m, 9000, 0.2m, 30));
    }

    [Fact]
    public async Task Roll_IsNotExecutableByTheAppRole()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(dedicated.Name));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"select public.roll_healing_llm_budget({Today})", connection);

        var failure = await Should.ThrowAsync<PostgresException>(
            () => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        failure.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Roll_WithAHoldInFlight_LetsItSettleOnTheDayItWasTakenOn()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedProjectAsync(dedicated.Name);

        // A call reserved 0.05 / 1035 on yesterday's row and is still running when the day turns.
        await SeedRowAsync(dedicated.Name, org, project, $"{Today} - 1", 1.0m, 5000, 0.05m, 1035);
        (await RollAsWorkerAsync(dedicated.Name)).ShouldBe(1);

        await SettleAsync(dedicated.Name, org, project, reservedTokens: 1035, reservedCost: 0.05m, actualTokens: 20, actualCost: 0.01m);

        // The hold is swapped for the real usage on its own day; the new day neither inherited nor lost it.
        (await ReadRowAsync(dedicated.Name, project, $"{Today} - 1")).ShouldBe((1.0m, 5000, 0.01m, 20));
        (await ReadRowAsync(dedicated.Name, project, $"{Today}")).ShouldBe((1.0m, 5000, 0m, 0));
    }

    private async Task<(Guid Org, Guid Project)> SeedProjectAsync(string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var slug = Unique.Slug("healing-reset");

        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await ExecuteAsync(
            connection,
            "insert into public.organization (id, name, slug) values (@id, @slug, @slug)",
            ("id", org),
            ("slug", slug));
        await ExecuteAsync(
            connection,
            "insert into public.project (id, org_id, name, slug) values (@id, @org, @slug, @slug)",
            ("id", project),
            ("org", org),
            ("slug", slug));

        return (org, project);
    }

    private async Task SeedRowAsync(
        string database,
        Guid org,
        Guid project,
        string periodStartSql,
        decimal capCost,
        int capTokens,
        decimal usedCost,
        int usedTokens)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await ExecuteAsync(
            connection,
            $"""
            insert into public.healing_llm_budget
                (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens, used_cost, used_tokens)
            values (@id, @org, @project, 'daily', {periodStartSql}, @cap_cost, @cap_tokens, @used_cost, @used_tokens)
            """,
            ("id", Guid.CreateVersion7()),
            ("org", org),
            ("project", project),
            ("cap_cost", capCost),
            ("cap_tokens", capTokens),
            ("used_cost", usedCost),
            ("used_tokens", usedTokens));
    }

    private async Task SpendTodayAsync(string database, Guid project, decimal cost, int tokens)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await ExecuteAsync(
            connection,
            $"update public.healing_llm_budget set used_cost = @cost, used_tokens = @tokens where project_id = @project and period_start = {Today}",
            ("cost", cost),
            ("tokens", tokens),
            ("project", project));
    }

    /// <summary>Runs the settlement statement the gateway runs, on the reservation's own period start.</summary>
    private async Task SettleAsync(
        string database,
        Guid org,
        Guid project,
        int reservedTokens,
        decimal reservedCost,
        int actualTokens,
        decimal actualCost)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        var periodStart = (DateTime)(await new NpgsqlCommand($"select {Today} - 1", connection)
            .ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        await ExecuteAsync(
            connection,
            HealingLlmBudgetSql.Settle,
            ("org_id", org),
            ("project_id", project),
            ("period_start", periodStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ("reserved_tokens", reservedTokens),
            ("reserved_cost", reservedCost),
            ("actual_tokens", actualTokens),
            ("actual_cost", actualCost));
    }

    /// <summary>Calls the roll the way the schedule does: delegate to <c>evidgate_worker_sched</c>.</summary>
    private async Task<int> RollAsWorkerAsync(string database)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await ExecuteAsync(connection, "set role evidgate_worker_sched");

        await using var command = new NpgsqlCommand($"select public.roll_healing_llm_budget({Today})", connection);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(decimal CapCost, int CapTokens, decimal UsedCost, int UsedTokens)> ReadRowAsync(
        string database,
        Guid project,
        string periodStartSql)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = new NpgsqlCommand(
            $"select period_cap_cost, period_cap_tokens, used_cost, used_tokens from public.healing_llm_budget where project_id = @p and period_start = {periodStartSql}",
            connection);
        command.Parameters.AddWithValue("p", project);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        return (reader.GetDecimal(0), reader.GetInt32(1), reader.GetDecimal(2), reader.GetInt32(3));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

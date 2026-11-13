using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The settings edge is a thin read: each route touches the AI-settings tables once per piece of information it answers,
/// so a list that re-queries inside a loop, or a read that opens a transaction per row, fails here.
/// </summary>
/// <remarks>
/// The bound is the plan's statement budget per route, asserted on the statements Npgsql actually executed within one
/// request: the head reads the row once, the history page reads its page once, and the catalogue is one statement. The
/// save is the only route that legitimately reads more than it writes (it validates the template, then inserts).
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsCommandCounterTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public AiSettingsCommandCounterTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    /// <summary>The three reads stay within their statement budget.</summary>
    [Fact]
    public async Task AiSettingsReads_StayWithinTheirBudget()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        (await CountAsync(world, () => AiSettingsCalls.GetAsync(world.Client, world.As, world.Settings)))
            .ShouldBeLessThanOrEqualTo(2, "the head is one row: one statement, plus at most one scope query");

        (await CountAsync(world, () => AiSettingsCalls.GetAsync(world.Client, world.As, world.Versions)))
            .ShouldBeLessThanOrEqualTo(2, "the history page is one keyset query");

        (await CountAsync(world, () => AiSettingsCalls.GetAsync(world.Client, world.As, world.Templates)))
            .ShouldBeLessThanOrEqualTo(2, "the catalogue is one query, not one per template");
    }

    /// <summary>A save touches the settings tables no more than the read it validates plus the insert it appends.</summary>
    [Fact]
    public async Task Save_StaysWithinItsBudget()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture, hosts: _hosts);

        var statements = await CountAsync(
            world,
            () => AiSettingsCalls.SaveAsync(
                world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document(), AiSettingsCalls.Template("default", 1)), "\"0\""));

        statements.ShouldBeLessThanOrEqualTo(4, "the template check, the head read and the row insert — never a query per field");
    }

    /// <summary>Counts the statements one request ran over the two AI-settings tables.</summary>
    private static async Task<int> CountAsync(AiSettingsCalls.World world, Func<Task<ApiAnswer>> call)
    {
        world.Api.Statements.Clear();
        var answer = await call();
        answer.Status.ShouldBeOneOf(
            [HttpStatusCode.OK, HttpStatusCode.Created],
            $"the probe must reach its handler before it is counted: {answer.Status} {answer.ErrorMessage}");

        return world.Api.Statements.Snapshot()
            .Count(statement => statement.Contains("project_ai_settings", StringComparison.Ordinal)
                || statement.Contains("ai_settings_template", StringComparison.Ordinal));
    }
}

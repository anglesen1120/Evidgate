using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class RunStateHistoryFixtureTests
{
    private static readonly string[] FromStates = ["queued", "running", "collecting"];
    private static readonly string[] ToStates = ["running", "collecting", "complete"];
    private static readonly string[] TransitionMembers = ["from_state", "occurred_at", "to_state"];

    [Fact]
    public async Task RecordedHistoryFixture_ContainsOnlyTransitionsAndEndsAtTheServedState()
    {
        using var document = await ReadFixtureAsync("run-state");
        document.RootElement.GetProperty("operation_id").GetString().ShouldBe("getRun");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(200);
        var body = document.RootElement.GetProperty("body");
        var history = body.GetProperty("state_history").EnumerateArray().ToArray();
        history.Length.ShouldBe(3);
        history.Select(item => item.GetProperty("from_state").GetString()).ShouldBe(FromStates);
        history.Select(item => item.GetProperty("to_state").GetString()).ShouldBe(ToStates);
        foreach (var item in history)
        {
            item.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)
                .ShouldBe(TransitionMembers);
            item.GetProperty("from_state").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("occurred_at").GetDateTimeOffset().Offset.ShouldBe(TimeSpan.Zero);
        }

        history[^1].GetProperty("to_state").GetString().ShouldBe(body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task LegacyRunFixtures_KeepUnknownHistoryExplicitlyNullInsteadOfEmpty()
    {
        var fixtures = new[]
        {
            "run-detail", "run-detail-null-evidence", "run-detail-ref-format",
            "run-state-onboarding-evidence", "run-state-onboarding-run-complete",
        };
        fixtures.Length.ShouldBeGreaterThanOrEqualTo(5);
        foreach (var fixture in fixtures)
        {
            using var document = await ReadFixtureAsync(fixture);
            document.RootElement.GetProperty("operation_id").GetString().ShouldBe("getRun");
            document.RootElement.GetProperty("status").GetInt32().ShouldBe(200);
            document.RootElement.GetProperty("body").GetProperty("state_history").ValueKind
                .ShouldBe(JsonValueKind.Null, fixture);
        }
    }

    private static async Task<JsonDocument> ReadFixtureAsync(string name)
    {
        var repository = Directory.GetParent(PostgresTenancyFixture.RepoMigrationsPath)
            ?? throw new InvalidOperationException("The migrations directory must belong to the repository");
        var text = await File.ReadAllTextAsync(
            Path.Combine(repository.FullName, "fixtures", "web-api", name, "input.json"),
            TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text);
    }
}

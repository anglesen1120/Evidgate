using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// <c>GET /v1/projects/{p}/runs/decision-counts</c> as the generated client reads it: one optional date-time
/// <c>since</c> whose inclusive boundary and default are written down, a closed response with all seven members
/// required, and the permission the route answers under.
/// </summary>
public sealed class ListRunDecisionCountsContractTests
{
    private const string PathKey = "/projects/{p}/runs/decision-counts";

    [Fact]
    public void Spec_DecisionCounts_IsARunReadGetWithTheProjectAndSinceParameters()
    {
        var operation = OpenApiSpec.Load().Operation(PathKey, "get");

        operation["operationId"].ShouldNotBeNull().GetValue<string>().ShouldBe("getRunDecisionCounts");
        operation["x-permission"].ShouldNotBeNull().GetValue<string>().ShouldBe("run:read");
        OpenApiSpec.Load().ParameterRefs(PathKey, "get").ShouldBe(["ProjectId", "RunDecisionCountsSince"], ignoreOrder: true);
    }

    [Fact]
    public void Spec_DecisionCounts_SinceIsAnOptionalDateTimeWithNoSchemaDefault()
    {
        var parameter = Since();

        parameter["required"].ShouldNotBeNull().GetValue<bool>().ShouldBeFalse();
        var schema = parameter["schema"].ShouldBeOfType<JsonObject>();
        schema["type"].ShouldNotBeNull().GetValue<string>().ShouldBe("string");
        schema["format"].ShouldNotBeNull().GetValue<string>().ShouldBe("date-time");
        schema.ContainsKey("default").ShouldBeFalse("the default is a moving window, not a constant");
    }

    [Fact]
    public void Spec_DecisionCounts_DocumentsTheInclusiveSinceBoundaryTheDefaultWindowAndTheScanCap()
    {
        var text = OpenApiSpec.Load().DocumentedText("getRunDecisionCounts");

        text.ShouldContain("(default now minus 30 days, inclusive `created_at >= since`)");
        text.ShouldContain("scans at most 5000 runs");
        text.ShouldContain("`truncated` is `true`");
        text.ShouldContain("without `decision:read` gets 403 `forbidden`");
    }

    [Fact]
    public void Spec_DecisionCounts_ResponseIsClosedAndRequiresAllSevenMembers()
    {
        var schema = OpenApiSpec.Load().Schema("RunDecisionCounts");
        string[] members = ["since", "pass", "fail", "inconclusive", "no_decision", "scanned", "truncated"];

        schema["additionalProperties"].ShouldNotBeNull().GetValue<bool>().ShouldBeFalse();
        var required = schema["required"].ShouldBeOfType<JsonArray>().Select(item => item!.GetValue<string>());
        required.ShouldBe(members, ignoreOrder: true);
        var properties = schema["properties"].ShouldBeOfType<JsonObject>();
        properties.Select(property => property.Key).ShouldBe(members, ignoreOrder: true);
        properties["truncated"]!["type"]!.GetValue<string>().ShouldBe("boolean");
        properties["since"]!["format"]!.GetValue<string>().ShouldBe("date-time");
    }

    [Fact]
    public void Spec_DecisionCounts_AnswersTheSharedErrorResponses()
    {
        OpenApiSpec.Load().ResponseKeys(PathKey, "get")
            .ShouldBe(["200", "401", "403", "404", "422", "429", "503"], ignoreOrder: true);
    }

    private static JsonObject Since() =>
        OpenApiSpec.Load().OperationParameters(PathKey, "get", "query")
            .Where(parameter => parameter["name"]?.GetValue<string>() == "since")
            .ShouldHaveSingleItem();
}

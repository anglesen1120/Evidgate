using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The run list's <c>order</c> and <c>completeness</c> query parameters as the generated client reads them: shared
/// component parameters, optional, with closed enums, and a default only for the order.
/// </summary>
public sealed class ListRunsContractTests
{
    private const string PathKey = "/projects/{p}/runs";

    [Fact]
    public void Spec_ListRuns_ReferencesTheSharedOrderAndCompletenessParameters()
    {
        var references = OpenApiSpec.Load().ParameterRefs(PathKey, "get");

        references.ShouldContain("RunListOrder");
        references.ShouldContain("RunCompleteness");
    }

    [Fact]
    public void Spec_ListRuns_OrderIsAnOptionalAscDescEnumDefaultingToAsc()
    {
        var parameter = QueryParameter("order");

        parameter["required"].ShouldNotBeNull().GetValue<bool>().ShouldBeFalse();
        var schema = parameter["schema"].ShouldBeOfType<JsonObject>();
        schema["type"].ShouldNotBeNull().GetValue<string>().ShouldBe("string");
        EnumValues(schema).ShouldBe(["asc", "desc"], ignoreOrder: false);
        schema["default"].ShouldNotBeNull().GetValue<string>().ShouldBe("asc");
    }

    [Fact]
    public void Spec_ListRuns_CompletenessIsAnOptionalCompleteIncompleteEnumWithNoDefault()
    {
        var parameter = QueryParameter("completeness");

        parameter["required"].ShouldNotBeNull().GetValue<bool>().ShouldBeFalse();
        var schema = parameter["schema"].ShouldBeOfType<JsonObject>();
        schema["type"].ShouldNotBeNull().GetValue<string>().ShouldBe("string");
        EnumValues(schema).ShouldBe(["complete", "incomplete"], ignoreOrder: false);
        schema.ContainsKey("default").ShouldBeFalse("an absent filter must stay absent, not select a value");
    }

    /// <summary>The one query parameter of <c>listRuns</c> with this name, its component reference resolved.</summary>
    private static JsonObject QueryParameter(string name) =>
        OpenApiSpec.Load().OperationParameters(PathKey, "get", "query")
            .Where(parameter => parameter["name"]?.GetValue<string>() == name)
            .ShouldHaveSingleItem();

    private static string[] EnumValues(JsonObject schema) =>
        schema["enum"] is JsonArray values
            ? [.. values.Select(value => value?.GetValue<string>() ?? throw new InvalidOperationException("null enum member"))]
            : throw new InvalidOperationException("the schema declares no enum");
}

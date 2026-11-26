using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The audit list's <c>project_id</c> query parameter as the generated client reads it: one shared component
/// parameter, optional, a uuid, and honest about what it covers.
/// </summary>
public sealed class AuditProjectFilterContractTests
{
    private const string ListPath = "/orgs/{o}/audit";
    private const string ExportPath = "/orgs/{o}/audit/export";

    [Fact]
    public void Spec_ListAudit_AndExportAudit_ShareTheProjectIdParameter()
    {
        var spec = OpenApiSpec.Load();

        spec.ParameterRefs(ListPath, "get").ShouldContain("AuditProjectId");
        spec.ParameterRefs(ExportPath, "get").ShouldContain("AuditProjectId");
    }

    [Fact]
    public void Spec_ListAudit_ProjectIdIsAnOptionalUuidWithNoDefault()
    {
        var parameter = QueryParameter("project_id");

        parameter["required"].ShouldNotBeNull().GetValue<bool>().ShouldBeFalse();
        var schema = parameter["schema"].ShouldBeOfType<JsonObject>();
        schema["type"].ShouldNotBeNull().GetValue<string>().ShouldBe("string");
        schema["format"].ShouldNotBeNull().GetValue<string>().ShouldBe("uuid");
        schema.ContainsKey("default").ShouldBeFalse("an absent filter must stay absent");
    }

    [Fact]
    public void Spec_ListAudit_DescribesTheProjectFilterCoverageAndThatTheTagIsOutsideTheHash()
    {
        var text = OpenApiSpec.Load().DocumentedText("listAudit");

        text.ShouldContain("only project-tagged actions recorded since the tag was introduced");
        text.ShouldContain("The tag is not part of `entry_hash`");
        text.ShouldContain("answers an empty page, not 404");
    }

    /// <summary>The one query parameter of <c>listAudit</c> with this name, its component reference resolved.</summary>
    private static JsonObject QueryParameter(string name) =>
        OpenApiSpec.Load().OperationParameters(ListPath, "get", "query")
            .Where(parameter => parameter["name"]?.GetValue<string>() == name)
            .ShouldHaveSingleItem();
}

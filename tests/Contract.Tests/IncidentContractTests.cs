using Shouldly;
using Xunit;

namespace Contract.Tests;

public sealed class IncidentContractTests
{
    [Fact]
    public void IncidentReads_DescribeTypedPagesFiltersAndDetail()
    {
        var spec = OpenApiSpec.Load();
        const string list = "/projects/{p}/incidents";
        const string detail = "/incidents/{i}";
        spec.ParameterRefs(list, "get").ShouldBe(["ProjectId", "Cursor", "Limit", "IncidentStateFilter", "IncidentExecutionKey"], ignoreOrder: true);
        spec.ResponseKeys(list, "get").ShouldContain("422");
        foreach (var (path, schema) in new[] { (list, "IncidentPage"), (detail, "Incident") })
        {
            var operation = spec.Operation(path, "get");
            operation["x-permission"]?.GetValue<string>().ShouldBe("healing:triage");
            operation["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>()
                .ShouldBe("#/components/schemas/" + schema);
        }

        spec.Schema("IncidentPage")["properties"]?["items"]?["items"]?["$ref"]?.GetValue<string>().ShouldBe("#/components/schemas/Incident");
        spec.Schema("Incident")["additionalProperties"]?.GetValue<bool>().ShouldBeFalse();
    }
}

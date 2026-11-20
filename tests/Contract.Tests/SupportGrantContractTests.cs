using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The support-grant wire contract (02.8.1 §5): the operator's request operation, the tenant's approve and revoke
/// (bodiless 200s, idempotent, <c>support:approve</c>), and the keyset list with its derived state. Shapes and
/// parameters are parsed from <c>contracts/openapi.yaml</c> through <see cref="OpenApiSpec"/> with component
/// references resolved; the refusal vocabulary is pinned as the documentation a client reads.
/// </summary>
public sealed class SupportGrantContractTests
{
    private const string RequestPath = "/support-grants";
    private const string ApprovePath = "/support-grants/{g}/approve";
    private const string RevokePath = "/support-grants/{g}/revoke";
    private const string ListPath = "/orgs/{o}/support-grants";

    /// <summary>The twelve fields the list view carries: the request, the derived state and the decision.</summary>
    private static readonly string[] ViewFields =
    [
        "id", "org_id", "operator_user_id", "scopes", "project_ids", "reason",
        "state", "approved_by", "approved_at", "expires_at", "revoked_at", "created_at",
    ];

    [Fact]
    public void Spec_Declares_TheFourSupportOperations()
    {
        var spec = OpenApiSpec.Load();

        spec.PathKeys.ShouldContain(RequestPath);
        spec.PathKeys.ShouldContain(ApprovePath);
        spec.PathKeys.ShouldContain(RevokePath);
        spec.PathKeys.ShouldContain(ListPath);
        spec.SchemaNames.ShouldContain("SupportGrant");
        spec.SchemaNames.ShouldContain("SupportGrantList");
    }

    /// <summary>
    /// The tenant's two decisions: no body at all (so nothing in the request can name an approver), the approver's
    /// permission, the key the replay store claims, and a 200 with no content. The refusal vocabulary a client reads
    /// is pinned beside them; the module suite proves the handlers produce it.
    /// </summary>
    [Fact]
    public void Decisions_AreBodilessIdempotentAndPrincipalOnly()
    {
        var spec = OpenApiSpec.Load();

        foreach (var path in new[] { ApprovePath, RevokePath })
        {
            var operation = spec.Operation(path, "post");
            operation["x-permission"]!.GetValue<string>().ShouldBe("support:approve");
            operation.ShouldNotContain(
                pair => pair.Key == "requestBody",
                $"{path} documents no body: the approver is the authenticated principal");
            spec.ParameterRefs(path, "post").ShouldContain("#/components/parameters/IdempotencyKey");
            spec.ParameterRefs(path, "post").ShouldContain("#/components/parameters/SupportGrantId");

            var responses = operation["responses"]!.AsObject();
            responses.ContainsKey("200").ShouldBeTrue();
            responses.ShouldNotContain(pair => pair.Key == "201" || pair.Key == "204");
            responses["200"]!.AsObject().ShouldNotContain(
                pair => pair.Key == "content",
                $"{path} answers 200 with no body");
            responses.ContainsKey("409").ShouldBeTrue();
            responses.ContainsKey("422").ShouldBeTrue();
        }

        spec.DocumentedText("approveSupportGrant").ShouldContain("403 `approver_not_tenant_admin`", Case.Sensitive);
        spec.DocumentedText("approveSupportGrant").ShouldContain("422 `approver_must_differ`", Case.Sensitive);
        spec.DocumentedText("approveSupportGrant").ShouldContain("409 `grant_not_requested`", Case.Sensitive);
        spec.DocumentedText("revokeSupportGrant").ShouldContain("409 `grant_already_revoked`", Case.Sensitive);
    }

    /// <summary>
    /// The operator's request operation is documented but not served by the tenant host in this change: the spec
    /// keeps it, and no tenant route claims it (the served route table and the isolation suite pin that side).
    /// </summary>
    [Fact]
    public void Request_IsTheOperatorSideAndNotATenantDecision()
    {
        var spec = OpenApiSpec.Load();
        var operation = spec.Operation(RequestPath, "post");

        operation["x-permission"]!.GetValue<string>().ShouldBe("support:grant");
        operation["requestBody"].ShouldNotBeNull("the operator names scope, reason and expiry");
        spec.ResponseKeys(RequestPath, "post").ShouldContain("201");
    }

    [Fact]
    public void List_DeclaresTheKeysetPageAndItsDerivedState()
    {
        var spec = OpenApiSpec.Load();
        var operation = spec.Operation(ListPath, "get");

        operation["x-permission"]!.GetValue<string>().ShouldBe("support:read");
        spec.ParameterRefs(ListPath, "get").ShouldContain("#/components/parameters/OrgId");
        spec.ParameterRefs(ListPath, "get").ShouldContain("#/components/parameters/Cursor");
        spec.ParameterRefs(ListPath, "get").ShouldContain("#/components/parameters/Limit");
        spec.ResponseKeys(ListPath, "get").ShouldContain("200");
        operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!
            .GetValue<string>()
            .ShouldBe("#/components/schemas/SupportGrantList");

        var list = spec.Schema("SupportGrantList");
        list["required"]!.AsArray().Select(entry => entry!.GetValue<string>()).ShouldBe(["items", "next_cursor"]);
        list["properties"]!["items"]!["items"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/components/schemas/SupportGrant");

        var view = spec.Schema("SupportGrant")["properties"]!.AsObject();
        view.Select(pair => pair.Key).Order(StringComparer.Ordinal).ShouldBe(ViewFields.Order(StringComparer.Ordinal));
        view["state"]!["enum"]!.AsArray().Select(entry => entry!.GetValue<string>())
            .ShouldBe(["requested", "approved", "revoked", "expired"], "expiry is derived, so it is a state a client reads");
        foreach (var decision in new[] { "approved_by", "approved_at", "revoked_at" })
        {
            view[decision]!["type"]!.AsArray().Select(entry => entry!.GetValue<string>())
                .ShouldContain("null", $"'{decision}' is absent while the grant is requested");
        }
    }
}

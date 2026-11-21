using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// The draft surface of 03.6.1: four operations, the request and view schemas their clients generate from, and the
/// bounds the content validator enforces. The served document is read as well, so the spec and the host cannot drift
/// apart without failing here.
/// </summary>
public sealed class DraftContractTests
{
    private const string CollectionPath = "/projects/{p}/drafts";
    private const string ObjectPath = "/drafts/{d}";

    /// <summary>The draft view's wire fields, in the DTO's declaration order.</summary>
    private static readonly string[] ViewFields =
    [
        "id", "project_id", "author_membership_id", "kind", "base_commit", "status", "version", "content",
        "provenance", "ai_run_id", "dry_run_id", "allow_unverified", "unverified_reason", "pr_branch",
        "pr_commit_sha", "pr_url", "pr_number", "linked_case_id", "linked_revision_id", "created_at", "updated_at",
    ];

    private static readonly string[] Statuses =
        ["draft", "dry_running", "dry_passed", "dry_failed", "pr_open", "merged", "closed"];

    [Fact]
    public void Spec_DeclaresTheFourDraftOperationsAndTheirSchemas()
    {
        var spec = OpenApiSpec.Load();

        spec.Operation(CollectionPath, "post")["operationId"]!.GetValue<string>().ShouldBe("createDraft");
        spec.Operation(CollectionPath, "get")["operationId"]!.GetValue<string>().ShouldBe("listDrafts");
        spec.Operation(ObjectPath, "patch")["operationId"]!.GetValue<string>().ShouldBe("updateDraft");
        spec.Operation(ObjectPath, "get")["operationId"]!.GetValue<string>().ShouldBe("getDraft");

        foreach (var schema in new[]
                 {
                     "DraftDto", "DraftContentDto", "DraftFileDto", "CreateDraftRequest", "UpdateDraftRequest",
                     "DraftListResponse",
                 })
        {
            spec.SchemaNames.ShouldContain(schema);
        }
    }

    [Fact]
    public void Spec_PinsThePermissionsBodiesAndConcurrencyHeaders()
    {
        var spec = OpenApiSpec.Load();

        spec.Operation(CollectionPath, "post")["x-permission"]!.GetValue<string>().ShouldBe("draft:write");
        spec.Operation(ObjectPath, "patch")["x-permission"]!.GetValue<string>().ShouldBe("draft:write");
        spec.Operation(CollectionPath, "get")["x-permission"]!.GetValue<string>().ShouldBe("case:read");
        spec.Operation(ObjectPath, "get")["x-permission"]!.GetValue<string>().ShouldBe("case:read");

        foreach (var (path, method) in new[] { (CollectionPath, "post"), (ObjectPath, "patch") })
        {
            spec.ParameterRefs(path, method).ShouldContain("IdempotencyKey");
            spec.Operation(path, method)["requestBody"].ShouldNotBeNull($"{method.ToUpperInvariant()} {path} documents its body");
        }

        spec.ParameterRefs(ObjectPath, "patch").ShouldContain("IfMatch");
        spec.ParameterRefs(ObjectPath, "patch").ShouldContain("DraftId");
        spec.ParameterRefs(ObjectPath, "get").ShouldContain("DraftId");

        spec.ResponseKeys(CollectionPath, "post").ShouldContain("201");
        spec.ResponseKeys(CollectionPath, "post").ShouldContain("413");
        spec.ResponseKeys(ObjectPath, "patch").ShouldContain("412");
        spec.ResponseKeys(ObjectPath, "patch").ShouldContain("409");
        spec.ResponseKeys(ObjectPath, "patch").ShouldContain("413");
        spec.ResponseKeys(ObjectPath, "get").ShouldContain("404");

        // The refusal vocabulary a client reads is pinned beside the statuses; the module suite proves the handlers
        // produce it. The 412's version detail is the one number a retry loop needs.
        spec.DocumentedText("createDraft").ShouldContain("draft_invalid", Case.Sensitive);
        spec.DocumentedText("createDraft").ShouldContain("draft_author_required", Case.Sensitive);
        spec.DocumentedText("createDraft").ShouldContain("409 `idempotency_conflict`", Case.Sensitive);
        spec.DocumentedText("updateDraft").ShouldContain("409 `draft_state_conflict`", Case.Sensitive);
    }

    [Fact]
    public void Spec_PinsTheViewAndTheContentBounds()
    {
        var spec = OpenApiSpec.Load();
        var view = spec.Schema("DraftDto")["properties"]!.AsObject();

        view.Select(pair => pair.Key).Order(StringComparer.Ordinal)
            .ShouldBe(ViewFields.Order(StringComparer.Ordinal), "DraftDto carries the twenty-one wire fields");
        view["status"]!["enum"]!.AsArray().Select(entry => entry!.GetValue<string>())
            .ShouldBe(Statuses, "the status set is the machine's seven states");
        foreach (var absent in new[] { "provenance", "ai_run_id", "dry_run_id", "unverified_reason", "pr_url", "pr_number" })
        {
            view[absent]!["type"]!.AsArray().Select(entry => entry!.GetValue<string>())
                .ShouldContain("null", $"'{absent}' is absent while the draft is new");
        }

        var files = spec.Schema("DraftContentDto")["properties"]!["files"]!;
        // YamlSubset stores every integer scalar as Int64 (`JsonValue.Create(long)`), so the contract tests read the
        // spec's numbers as longs; the served document, parsed by System.Text.Json, is where Int32 applies.
        files["maxItems"]!.GetValue<long>().ShouldBe(200L);
        files["minItems"]!.GetValue<long>().ShouldBe(1L);

        var file = spec.Schema("DraftFileDto")["properties"]!;
        file["path"]!["maxLength"]!.GetValue<long>().ShouldBe(512L);
        file["path"]!["minLength"]!.GetValue<long>().ShouldBe(1L);
        file["body_sha"]!["pattern"]!.GetValue<string>().ShouldBe("^sha256:[0-9a-f]{64}$");
        file["body_ref"]!["maxLength"]!.GetValue<long>().ShouldBe(512L);

        var request = spec.Schema("CreateDraftRequest")["properties"]!;
        request["base_commit"]!["pattern"]!.GetValue<string>().ShouldBe("^[0-9a-f]{7,64}$");
        request["kind"]!["enum"]!.AsArray().Select(entry => entry!.GetValue<string>())
            .ShouldBe(["template", "ai", "regression"]);
    }

    [Fact]
    public async Task Served_Document_CarriesTheFourOperations()
    {
        await using var factory = new EvidgateApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");

        // The served document carries no operationId (the SDK ids come from the spec file), so the route table is the
        // served half of this pin: each draft path and method must be present.
        (string Path, string Method)[] operations =
        [
            ("/v1/projects/{p}/drafts", "post"),
            ("/v1/projects/{p}/drafts", "get"),
            ("/v1/drafts/{d}", "patch"),
            ("/v1/drafts/{d}", "get"),
        ];
        foreach (var (path, method) in operations)
        {
            paths.GetProperty(path).TryGetProperty(method, out _).ShouldBeTrue($"{method} {path} is served");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var view = schemas.GetProperty("DraftDto").GetProperty("properties").EnumerateObject()
            .Select(member => member.Name).ToList();
        view.ShouldBe(ViewFields.ToList(), "the served DraftDto carries the same fields, in the same order");
    }
}

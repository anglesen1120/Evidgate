using System.Text.Json;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Tenancy.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.1.1-T1.3: the wire shape of <see cref="OrganizationDto"/> and
/// <see cref="ProjectDto"/> is pinned as JSON.
/// </summary>
/// <remarks>
/// <c>contracts/openapi.yaml</c> declares no <c>Organization</c> and no
/// <c>Project</c> component schema yet (only the <c>/orgs/{o}/projects</c> path,
/// whose create response is <c>{project_id}</c>), and 02.1.1-T1 must not edit the
/// contract file. The authority for the field list is therefore the feature plan
/// §4 plus the 00 §7 column contract; 02.1.1-T2 owns publishing both schemas and
/// extending this test to read them.
/// </remarks>
public sealed class DtoContractTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 26, 12, 30, 0, TimeSpan.Zero);

    /// <summary>Plan §4: id, name, slug, status + the org-only retention/domain fields and the 00 §7 columns.</summary>
    [Fact]
    public void Organization_Dto_Matches_Plan_Shape()
    {
        var dto = new OrganizationDto(
            Guid.Parse("018f0000-0000-7000-8000-000000000001"),
            "Acme",
            "acme",
            "active",
            30,
            ["acme.example"],
            Timestamp,
            Timestamp,
            3,
            10,
            25);

        JsonSerializer.Serialize(dto, SnakeCase).ShouldBe(
            """
            {"id":"018f0000-0000-7000-8000-000000000001","name":"Acme","slug":"acme","status":"active","retention_default_days":30,"allowed_email_domains":["acme.example"],"created_at":"2026-09-26T12:30:00+00:00","updated_at":"2026-09-26T12:30:00+00:00","version":3,"max_projects":10,"max_seats":25}
            """);

        // The optional columns carry null, never a sentinel value (plan §4: both
        // columns are nullable in the migration).
        JsonSerializer.Serialize(
                dto with { RetentionDefaultDays = null, AllowedEmailDomains = null },
                SnakeCase)
            .ShouldContain("\"retention_default_days\":null,\"allowed_email_domains\":null", Case.Sensitive);
    }

    /// <summary>Plan §4: the project carries org_id, status, the environment pointer and the 00 §7 columns.</summary>
    [Fact]
    public void Project_Dto_Matches_Plan_Shape()
    {
        // `case_prefix` is the last member so every earlier position (and so Dapper's positional match) is unchanged; it
        // is 13.1.1 AC4/OI-13-101's stored code prefix (0039), read-only outside PATCH /v1/projects/{p}.
        var dto = new ProjectDto(
            Guid.Parse("018f0000-0000-7000-8000-000000000002"),
            Guid.Parse("018f0000-0000-7000-8000-000000000001"),
            "Checkout",
            "checkout",
            "active",
            Guid.Parse("018f0000-0000-7000-8000-000000000003"),
            Timestamp,
            Timestamp,
            1,
            90,
            1000,
            "CHEC");

        JsonSerializer.Serialize(dto, SnakeCase).ShouldBe(
            """
            {"id":"018f0000-0000-7000-8000-000000000002","org_id":"018f0000-0000-7000-8000-000000000001","name":"Checkout","slug":"checkout","status":"active","default_environment_id":"018f0000-0000-7000-8000-000000000003","created_at":"2026-09-26T12:30:00+00:00","updated_at":"2026-09-26T12:30:00+00:00","version":1,"retention_days":90,"monthly_run_quota":1000,"case_prefix":"CHEC"}
            """);

        // default_environment_id is null until 05.1.2-T1 adds the FK target.
        JsonSerializer.Serialize(dto with { DefaultEnvironmentId = null }, SnakeCase)
            .ShouldContain("\"default_environment_id\":null", Case.Sensitive);
    }

    /// <summary>
    /// 13.1.1-T2.4: <c>NodeDto</c> and <c>AuthoringTreeResponse</c> (routes 1-5) pin the member names and their order of
    /// the <c>NodeDto</c> and <c>AuthoringTreeResponse</c> schemas in <c>contracts/openapi.yaml</c>. The tree's
    /// <c>node</c>/<c>path</c> members are always written (null when not addressed), so a client can rely on the shape.
    /// </summary>
    [Fact]
    public void AuthoringTree_Dtos_Match_The_OpenApi_Schemas()
    {
        var node = new NodeDto(
            Guid.Parse("018f0000-0000-7000-8000-000000000101"),
            Guid.Parse("018f0000-0000-7000-8000-000000000102"),
            "feature",
            "Payment",
            2,
            3,
            1,
            4);

        JsonSerializer.Serialize(node, SnakeCase).ShouldBe(
            """
            {"node_id":"018f0000-0000-7000-8000-000000000101","parent_id":"018f0000-0000-7000-8000-000000000102","kind":"feature","title":"Payment","position":2,"version":3,"child_count":1,"item_count":4}
            """);

        // A root module carries a null parent (the schema's [uuid, null]).
        JsonSerializer.Serialize(node with { ParentId = null }, SnakeCase)
            .ShouldContain("\"node_id\":\"018f0000-0000-7000-8000-000000000101\",\"parent_id\":null", Case.Sensitive);

        var tree = new AuthoringTreeResponse(
            [node],
            null,
            node,
            [new NodePathEntry(Guid.Parse("018f0000-0000-7000-8000-000000000103"), "module", "Checkout")]);

        JsonSerializer.Serialize(tree, SnakeCase).ShouldBe(
            """
            {"items":[{"node_id":"018f0000-0000-7000-8000-000000000101","parent_id":"018f0000-0000-7000-8000-000000000102","kind":"feature","title":"Payment","position":2,"version":3,"child_count":1,"item_count":4}],"next_cursor":null,"node":{"node_id":"018f0000-0000-7000-8000-000000000101","parent_id":"018f0000-0000-7000-8000-000000000102","kind":"feature","title":"Payment","position":2,"version":3,"child_count":1,"item_count":4},"path":[{"node_id":"018f0000-0000-7000-8000-000000000103","kind":"module","title":"Checkout"}]}
            """);

        // The listing form keeps the two members and writes both as null, never omitting them.
        JsonSerializer.Serialize(new AuthoringTreeResponse([], null, null, null), SnakeCase)
            .ShouldBe("""{"items":[],"next_cursor":null,"node":null,"path":null}""");
    }

    /// <summary>
    /// 13.1.1-T2.4: the three authoring answer records of a write (<c>AuthoringChangeResponse</c> for the PATCH routes,
    /// <c>ReorderNodesResponse</c>, <c>CreateNodeResponse</c>) pin the member names and order of the
    /// <c>AuthoringChangeResponse</c>, <c>ReorderNodesResponse</c> and <c>CreateNodeResponse</c> schemas.
    /// </summary>
    [Fact]
    public void AuthoringNodeAnswers_Match_The_OpenApi_Schemas()
    {
        JsonSerializer.Serialize(new AuthoringChangeResponse(2, true), SnakeCase)
            .ShouldBe("""{"version":2,"changed":true}""");
        JsonSerializer.Serialize(new ReorderNodesResponse(false), SnakeCase)
            .ShouldBe("""{"changed":false}""");
        JsonSerializer.Serialize(
                new CreateNodeResponse(Guid.Parse("018f0000-0000-7000-8000-000000000101"), 1),
                SnakeCase)
            .ShouldBe("""{"node_id":"018f0000-0000-7000-8000-000000000101","version":1}""");
    }

    /// <summary>
    /// 13.1.1-T2.4: <c>ChecklistItemDto</c> and its two write answers pin the member names and order of the
    /// <c>ChecklistItemDto</c>, <c>CreateChecklistItemResponse</c> and <c>AuthoringChangeResponse</c> schemas: the list
    /// row carries <c>source</c> and <c>version</c>, so a client can send <c>If-Match</c> without a second read.
    /// </summary>
    [Fact]
    public void ChecklistItem_Dtos_Match_The_OpenApi_Schemas()
    {
        var item = new ChecklistItemDto(
            Guid.Parse("018f0000-0000-7000-8000-000000000201"),
            Guid.Parse("018f0000-0000-7000-8000-000000000202"),
            "Card form",
            null,
            "high",
            "active",
            "manual",
            0,
            1);

        JsonSerializer.Serialize(item, SnakeCase).ShouldBe(
            """
            {"item_id":"018f0000-0000-7000-8000-000000000201","node_id":"018f0000-0000-7000-8000-000000000202","title":"Card form","description":null,"priority":"high","state":"active","source":"manual","position":0,"version":1}
            """);

        JsonSerializer.Serialize(
                new CreateChecklistItemResponse(Guid.Parse("018f0000-0000-7000-8000-000000000201"), 1, "active"),
                SnakeCase)
            .ShouldBe("""{"item_id":"018f0000-0000-7000-8000-000000000201","version":1,"state":"active"}""");
    }

    /// <summary>
    /// 13.1.1-T2.4: the manual case list row, detail and their three write answers pin the member names and order of the
    /// <c>ManualCaseListItemDto</c>, <c>ManualCaseDetailDto</c>, <c>CreateManualCaseResponse</c>,
    /// <c>SetManualCaseStateResponse</c> and <c>MoveManualCaseResponse</c> schemas. The detail carries no
    /// <c>links</c>/coverage member: those are 13.1.1-T4's.
    /// </summary>
    [Fact]
    public void ManualCase_Dtos_Match_The_OpenApi_Schemas()
    {
        var caseId = Guid.Parse("018f0000-0000-7000-8000-000000000301");
        var itemId = Guid.Parse("018f0000-0000-7000-8000-000000000201");
        var nodeId = Guid.Parse("018f0000-0000-7000-8000-000000000202");

        JsonSerializer.Serialize(
                new ManualCaseListItemDto(caseId, "CHEC-001", itemId, nodeId, "Pay by card", "critical", "draft", 2),
                SnakeCase)
            .ShouldBe(
                """
                {"case_id":"018f0000-0000-7000-8000-000000000301","code":"CHEC-001","checklist_item_id":"018f0000-0000-7000-8000-000000000201","node_id":"018f0000-0000-7000-8000-000000000202","title":"Pay by card","priority":"critical","state":"draft","version":2}
                """);

        var detail = new ManualCaseDetailDto(
            caseId,
            "CHEC-001",
            itemId,
            nodeId,
            "Pay by card",
            null,
            "A cart with one item",
            [new ManualCaseStepDto(1, "Open the cart", "The cart is shown", "4111")],
            "high",
            "draft",
            1,
            Timestamp,
            Timestamp);

        JsonSerializer.Serialize(detail, SnakeCase).ShouldBe(
            """
            {"case_id":"018f0000-0000-7000-8000-000000000301","code":"CHEC-001","checklist_item_id":"018f0000-0000-7000-8000-000000000201","node_id":"018f0000-0000-7000-8000-000000000202","title":"Pay by card","description":null,"precondition":"A cart with one item","steps":[{"no":1,"action":"Open the cart","expected":"The cart is shown","data":"4111"}],"priority":"high","state":"draft","version":1,"created_at":"2026-09-26T12:30:00+00:00","updated_at":"2026-09-26T12:30:00+00:00"}
            """);

        JsonSerializer.Serialize(
                new CreateManualCaseResponse(caseId, "CHEC-001", 1),
                SnakeCase)
            .ShouldBe(
                """{"case_id":"018f0000-0000-7000-8000-000000000301","code":"CHEC-001","version":1}""");
        JsonSerializer.Serialize(new SetManualCaseStateResponse("approved", 3), SnakeCase)
            .ShouldBe("""{"state":"approved","version":3}""");
        JsonSerializer.Serialize(new MoveManualCaseResponse(4, itemId), SnakeCase)
            .ShouldBe(
                """{"version":4,"checklist_item_id":"018f0000-0000-7000-8000-000000000201"}""");
    }

    /// <summary>
    /// 13.1.1-T2.4: <c>ManualCaseStepDto</c> matches the <c>ManualCaseStepDto</c> schema — <c>no</c>, <c>action</c>,
    /// <c>expected</c> in that order, with <c>data</c> optional. A step without data omits the member (the schema's
    /// <c>required</c> list is the first three), so an absent datum is never a <c>"data": null</c> the validator of
    /// <c>authoring-steps.v1.json</c> would have to accept.
    /// </summary>
    [Fact]
    public void ManualCaseStep_Dto_Matches_The_OpenApi_Schema()
    {
        JsonSerializer.Serialize(new ManualCaseStepDto(2, "Enter the card", "The card is accepted"), SnakeCase)
            .ShouldBe("""{"no":2,"action":"Enter the card","expected":"The card is accepted"}""");
        JsonSerializer.Serialize(new ManualCaseStepDto(2, "Enter the card", "The card is accepted", null), SnakeCase)
            .ShouldNotContain("\"data\"", Case.Sensitive);
    }

    internal static JsonSerializerOptions SnakeCase { get; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}


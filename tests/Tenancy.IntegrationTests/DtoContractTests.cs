using System.Text.Json;
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

    internal static JsonSerializerOptions SnakeCase { get; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

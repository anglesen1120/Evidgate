// 06.2.1: the audited kind-policy override route. Every request is a member principal inside the C2 pipeline, and
// every assertion reads the committed row back as the migration role so "unchanged" is proven, not inferred.
using System.Net;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.2.1 §6: <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c> — the one route that can lower a sensitive
/// default, so it requires <c>project:write</c>, writes the whole row, refuses an unknown kind with the named 422,
/// refuses a body that leaves a knob out, and records the previous and new sensitivity of every write.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceSensitivityTests
{
    /// <summary>A non-admin (read-only on the project) is refused before any row is written.</summary>
    [Fact]
    public async Task SetPolicyOverride_NonAdmin_403()
    {
        await using var world = await StartAsync();
        var engineer = await AddMemberAsync(world, "qa_engineer");
        var member = world.MemberClient(engineer);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, ArtifactKind.Trace), member.Caller, PolicyBody("normal"));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).ShouldBeEmpty(
            "a refused write writes neither the row nor its audit entry");
    }

    /// <summary>Lowering a kind's sensitive default is an explicit, audited write: both directions are recorded with
    /// the previous and the new sensitivity.</summary>
    [Fact]
    public async Task SetPolicyOverride_LowerTraceToNormal_WritesAuditRow()
    {
        await using var world = await StartAsync();
        await SeedPolicyAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive);
        var member = world.MemberClient(world.Founder);

        var lowered = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, ArtifactKind.Trace), member.Caller, PolicyBody("normal"));
        lowered.Status.ShouldBe(HttpStatusCode.OK);
        lowered.Body.GetProperty("kind").GetString().ShouldBe(ArtifactKind.Trace);
        lowered.Body.GetProperty("default_sensitivity").GetString().ShouldBe(ArtifactSensitivity.Normal);
        lowered.Body.GetProperty("capture_enabled").GetBoolean().ShouldBeTrue();

        var raised = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, ArtifactKind.Trace), member.Caller,
            PolicyBody("sensitive", captureEnabled: false, rejectOnSecret: true));
        raised.Status.ShouldBe(HttpStatusCode.OK);
        raised.Body.GetProperty("default_sensitivity").GetString().ShouldBe(ArtifactSensitivity.Sensitive);
        raised.Body.GetProperty("capture_enabled").GetBoolean().ShouldBeFalse();
        raised.Body.GetProperty("max_bytes").GetInt64().ShouldBe(2048);

        (await PolicyRowsAsync(world, world.Project)).ShouldBe(["trace|2048|sensitive|false|true"]);

        var audits = await AuditRowsAsync(world, "artifact_kind_policy.set", $"{world.Project:D}:{ArtifactKind.Trace}");
        audits.Count.ShouldBe(2, "one entry per write, including the lowering");
        audits[0].ActorType.ShouldBe("user");
        audits[0].ActorId.ShouldBe(world.Founder.ToString("D"));
        audits[0].ObjectType.ShouldBe("artifact_kind_policy");
        audits[0].PreviousDefaultSensitivity.ShouldBe(ArtifactSensitivity.Sensitive);
        audits[0].Status.ShouldBe(ArtifactSensitivity.Normal);
        audits[1].PreviousDefaultSensitivity.ShouldBe(ArtifactSensitivity.Normal);
        audits[1].Status.ShouldBe(ArtifactSensitivity.Sensitive);
    }

    /// <summary>An unknown kind is the named 422 the route promises, not a route miss and not a row.</summary>
    [Fact]
    public async Task SetPolicyOverride_UnknownKind_422()
    {
        await using var world = await StartAsync();

        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, "not_a_kind"), member.Caller, PolicyBody("normal"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("kind_unknown");
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
    }

    /// <summary>A body that leaves a knob out is refused and the stored row keeps every value it had.</summary>
    [Fact]
    public async Task SetPolicyOverride_MissingMember_RefusedAndPolicyUnchanged()
    {
        await using var world = await StartAsync();
        await SeedPolicyAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive);
        var member = world.MemberClient(world.Founder);

        var noCapture = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, ArtifactKind.Trace), member.Caller,
            """{"max_bytes":4096,"media_types":["application/zip"],"default_sensitivity":"normal","reject_on_secret":true}""");
        noCapture.Status.ShouldBe(HttpStatusCode.BadRequest);
        noCapture.ErrorCode.ShouldBe("request_invalid");

        var noReject = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, ArtifactKind.Trace), member.Caller,
            """{"max_bytes":4096,"media_types":["application/zip"],"default_sensitivity":"normal","capture_enabled":true}""");
        noReject.Status.ShouldBe(HttpStatusCode.BadRequest);
        noReject.ErrorCode.ShouldBe("request_invalid");

        (await PolicyRowsAsync(world, world.Project)).ShouldBe(
            ["trace|4096|sensitive|true|true"], "a refused body never rewrites a knob");
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).ShouldBeEmpty();
    }

    /// <summary>Another tenant's project is the same 404 as an unknown project, and the refused writes leave no row
    /// in either org.</summary>
    [Fact]
    public async Task SetPolicyOverride_OtherTenantProject_404AndWritesNothing()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject, _) = await CreateOtherTenantAsync(world);
        var member = world.MemberClient(world.Founder);

        var foreign = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(otherProject, ArtifactKind.Trace), member.Caller, PolicyBody("normal"));
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");

        var unknown = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(Guid.CreateVersion7(), ArtifactKind.Trace), member.Caller,
            PolicyBody("normal"));
        unknown.Status.ShouldBe(foreign.Status);
        unknown.ErrorCode.ShouldBe(foreign.ErrorCode);

        (await PolicyRowsAsync(world, otherProject, otherOrg)).ShouldBeEmpty();
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
    }

    /// <summary>A request narrowed to its own project cannot rewrite a sibling project's policy: the scope refusal is
    /// the same <c>project_scope_mismatch</c> the retention routes answer, and it leaves every row as it was.</summary>
    [Fact]
    public async Task SetPolicyOverride_OtherProjectOfTheSameOrg_ProjectScopedRefused()
    {
        await using var world = await StartAsync();
        var otherProject = await AddProjectAsync(world);
        await SeedPolicyAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Sensitive, project: otherProject);

        var answer = await world.ClientFor(RequestPrincipal.Member(world.Founder)).SendRawAsync(
            HttpMethod.Put, PolicyUrl(otherProject, ArtifactKind.Trace), NoCaller,
            PolicyBody("normal", rejectOnSecret: true));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
        (await PolicyRowsAsync(world, otherProject)).ShouldBe(["raw_report|4096|sensitive|true|true"]);
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).ShouldBeEmpty(
            "a refused write writes neither the row nor its audit entry");
    }

    /// <summary>A member's request carries an org and no project, so the route's own project is the scope it may
    /// write: a sibling project of the same org gains only its own rows and the member's own project stays empty.</summary>
    [Fact]
    public async Task SetPolicyOverride_OtherProjectOfTheSameOrg_MemberWritesOnlyTheRoutesRows()
    {
        await using var world = await StartAsync();
        var otherProject = await AddProjectAsync(world);
        await SeedPolicyAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Sensitive, project: otherProject);
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(otherProject, ArtifactKind.Trace), member.Caller,
            PolicyBody("normal", rejectOnSecret: true));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        (await PolicyRowsAsync(world, otherProject)).ShouldBe(
            ["raw_report|4096|sensitive|true|true", "trace|2048|normal|true|true"]);
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty(
            "the route's project is the row's project, never another one");
    }

    /// <summary>
    /// <c>project:write</c> is outside the service ceiling too, so a project-scoped service credential never reaches
    /// the handler: the pipeline refuses it 403 and writes neither the row nor an audit entry.
    /// </summary>
    [Fact]
    public async Task SetPolicyOverride_ProjectScopedServiceCredential_403BeforeAnyRow()
    {
        await using var world = await StartAsync();
        var service = world.ServiceClient(world.Project, "project:write");

        var answer = await service.Client.SendRawAsync(
            HttpMethod.Put, PolicyUrl(world.Project, ArtifactKind.Trace), service.Caller, PolicyBody("normal"));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        (await PolicyRowsAsync(world, world.Project)).ShouldBeEmpty();
        (await AuditRowsAsync(world, "artifact_kind_policy.set")).ShouldBeEmpty();
    }

    /// <summary>The override route's URL for one project and kind.</summary>
    private static string PolicyUrl(Guid project, string kind) =>
        $"/v1/projects/{project:D}/artifact-kind-policy/{kind}";

    /// <summary>A complete policy body: all five knobs, snake_case as the contract spells them.</summary>
    private static string PolicyBody(string sensitivity, bool captureEnabled = true, bool rejectOnSecret = false) =>
        $$"""
        {"max_bytes":2048,"media_types":["application/zip"],"default_sensitivity":"{{sensitivity}}","capture_enabled":{{(captureEnabled ? "true" : "false")}},"reject_on_secret":{{(rejectOnSecret ? "true" : "false")}}}
        """;
}

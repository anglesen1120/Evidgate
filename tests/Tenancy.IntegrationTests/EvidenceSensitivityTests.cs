// 06.2.1: the artifact read and download routes through the real Api composition, the real C2 pipeline,
// the real evaluator and a migrated PostgreSQL. Every caller is a persisted member of the org, because
// artifact:read is outside ServiceRoleCeiling.
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.2.1 (AC1–AC7) through the real host: the gated read model behind the metadata route, the
/// <c>download-url</c> gate with its denial and grant audit rows, cross-tenant and cross-project invisibility,
/// and the audited kind-policy override. Rows are seeded as the migration role, requests carry a member principal
/// the evaluator resolves against the real membership tables, and the object store is the CPU-only presigner
/// (SigV4 arithmetic, no container).
/// </summary>
/// <remarks>
/// A member's request is resolved by the production <c>TenantResolver</c>, which answers an org and no project
/// (<c>TenantResolver.ResolveMemberAsync</c>), so the reads here authorize against the artifact's own project; the
/// project-scoped form — a tenant published with a project — is used only to prove that a caller narrowed to another
/// project is refused identically to an unknown id.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class EvidenceSensitivityTests
{
    /// <summary>AC1: a viewer who holds <c>artifact:read</c> and not <c>artifact:read_sensitive</c> is answered a
    /// masked 200 for a sensitive artifact — the key, checksum and byte count are absent, never 403. The caller is
    /// resolved exactly as the production resolver scopes a member: an org and no project.</summary>
    [Fact]
    public async Task GetArtifact_Sensitive_WithoutPermission_ContentGated()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = await AddMemberAsync(world, "viewer");
        var member = world.MemberClient(viewer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK, "only content is gated: the metadata read stays a 200");
        answer.Body.GetProperty("content_gated").GetBoolean().ShouldBeTrue();
        answer.Body.GetProperty("sensitivity").GetString().ShouldBe(ArtifactSensitivity.Sensitive);
        answer.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);
        answer.Body.TryGetProperty("object_key", out _).ShouldBeFalse("a gated body omits the storage key");
        answer.Body.TryGetProperty("checksum", out _).ShouldBeFalse("a gated body omits the content address");
        answer.Body.TryGetProperty("bytes", out _).ShouldBeFalse("a gated body omits the byte count");
        answer.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue("a gated body is still a per-caller answer");
    }

    /// <summary>AC1 positive control: the same read with <c>artifact:read_sensitive</c> carries the content fields.</summary>
    [Fact]
    public async Task GetArtifact_Sensitive_WithPermission_ShowsContent()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var engineer = await AddMemberAsync(world, "qa_engineer");
        var member = world.MemberClient(engineer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("content_gated").GetBoolean().ShouldBeFalse();
        answer.Body.GetProperty("object_key").GetString().ShouldBe(seeded.ObjectKey);
        answer.Body.GetProperty("checksum").GetString().ShouldBe(Checksum);
        answer.Body.GetProperty("bytes").GetInt64().ShouldBe(Bytes);
    }

    /// <summary>Tenant isolation: another org's artifact id is the same 404 an unknown id answers.</summary>
    [Fact]
    public async Task GetArtifact_CrossTenant_404()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject, otherRun) = await CreateOtherTenantAsync(world);
        var foreign = await SeedArtifactAsync(
            world, ArtifactKind.Trace, ArtifactSensitivity.Normal, "clean",
            org: otherOrg, project: otherProject, runId: otherRun);
        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{foreign.Id:D}", member.Caller);
        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("artifact_not_found");

        var unknown = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{Guid.CreateVersion7():D}", member.Caller);
        unknown.Status.ShouldBe(answer.Status, "a foreign id and an unknown id must be one answer");
        unknown.ErrorCode.ShouldBe(answer.ErrorCode);
    }

    /// <summary>The route names no project, so the row's own project decides the scope: a member (org-level roles,
    /// no project in the request) reads a normal artifact of a sibling project, while the same member's request
    /// narrowed to its own project answers the one 404 for it — never the row.</summary>
    [Fact]
    public async Task GetArtifact_OtherProjectOfTheSameOrg_MemberReadsAndNarrowedRequest404()
    {
        await using var world = await StartAsync();
        var otherProject = await AddProjectAsync(world);
        var seeded = await SeedArtifactAsync(
            world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean", project: otherProject);

        var member = world.MemberClient(world.Founder);
        var metadata = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.OK, "a member's roles apply to every project of the org");
        metadata.Body.GetProperty("content_gated").GetBoolean().ShouldBeFalse();

        var download = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        download.Status.ShouldBe(HttpStatusCode.OK);

        var narrowed = world.ClientFor(RequestPrincipal.Member(world.Founder));
        var narrowedMetadata = await narrowed.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", NoCaller);
        narrowedMetadata.Status.ShouldBe(HttpStatusCode.NotFound);
        narrowedMetadata.ErrorCode.ShouldBe("artifact_not_found");

        var narrowedDownload = await narrowed.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", NoCaller);
        narrowedDownload.Status.ShouldBe(HttpStatusCode.NotFound);
        narrowedDownload.ErrorCode.ShouldBe("artifact_not_found");
    }

    /// <summary>
    /// The member form of both read routes: a request that carries an org and no project is authorized against the
    /// artifact's own project, so the metadata and the download of a normal artifact are a 200 for that member.
    /// </summary>
    [Fact]
    public async Task GetArtifact_Normal_MemberWithoutProjectScope_200OnMetadataAndDownloadUrl()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean");
        var viewer = await AddMemberAsync(world, "viewer");
        var member = world.MemberClient(viewer);

        var metadata = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.OK);
        metadata.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);
        metadata.Body.GetProperty("content_gated").GetBoolean().ShouldBeFalse();
        metadata.Body.GetProperty("object_key").GetString().ShouldBe(seeded.ObjectKey);

        var download = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        download.Status.ShouldBe(HttpStatusCode.OK);
        download.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);
    }

    /// <summary>
    /// <c>artifact:read</c> is outside the service ceiling, so a project-scoped service credential can never stand in
    /// for a member on either read route: the C2 stage refuses it before any row, exactly as an unknown id would, and
    /// writes no denial audit row. This is the ceiling's promise, not the project scope's — the scope rule only ever
    /// sees a caller that already holds the permission.
    /// </summary>
    [Fact]
    public async Task GetArtifact_ProjectScopedServiceCredential_403BeforeAnyRow()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.RawReport, ArtifactSensitivity.Normal, "clean");
        var service = world.ServiceClient(world.Project, "artifact:read");

        var metadata = await service.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", service.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.Forbidden);
        metadata.ErrorCode.ShouldBe("forbidden");

        var unknown = await service.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{Guid.CreateVersion7():D}", service.Caller);
        unknown.Status.ShouldBe(metadata.Status);
        unknown.ErrorCode.ShouldBe(metadata.ErrorCode);

        var download = await service.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", service.Caller);
        download.Status.ShouldBe(HttpStatusCode.Forbidden);
        download.ErrorCode.ShouldBe("forbidden");

        (await AuditRowsAsync(world, "artifact.download_denied")).ShouldBeEmpty(
            "a caller without artifact:read must not be able to probe for rows");
    }

    /// <summary>AC2: a download of a sensitive artifact without the permission is 403 <c>sensitivity_forbidden</c>,
    /// and the denial's audit row is committed before the refusal renders.</summary>
    [Fact]
    public async Task DownloadUrl_Sensitive_WithoutPermission_403AndAudited()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = await AddMemberAsync(world, "viewer");

        var member = world.MemberClient(viewer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("sensitivity_forbidden");

        var audits = await AuditRowsAsync(world, "artifact.download_denied", seeded.Id.ToString("D"));
        var row = audits.ShouldHaveSingleItem();
        row.ActorType.ShouldBe("user");
        row.ActorId.ShouldBe(viewer.ToString("D"));
        row.ObjectType.ShouldBe("artifact");
        row.ObjectId.ShouldBe(seeded.Id.ToString("D"));
        string.IsNullOrWhiteSpace(row.RequestId).ShouldBeFalse("the denial carries the request id");
        row.Outcome.ShouldBe("sensitive_forbidden");
        row.RunId.ShouldBe(world.RunId.ToString("D"), "the denial names the run the refused artifact belongs to");
    }

    /// <summary>AC3: the same route with <c>artifact:read_sensitive</c> issues a short-lived presigned URL and
    /// records the sensitive grant.</summary>
    [Fact]
    public async Task DownloadUrl_Sensitive_WithPermission_200()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var engineer = await AddMemberAsync(world, "qa_engineer");

        var member = world.MemberClient(engineer);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("artifact_id").GetGuid().ShouldBe(seeded.Id);

        var url = answer.Body.GetProperty("download_url").GetString() ?? string.Empty;
        url.ShouldStartWith("http://127.0.0.1:9/evidgate-staging/");
        url.ShouldContain(seeded.ObjectKey, Case.Sensitive);
        url.ShouldContain("X-Amz-Signature=", Case.Sensitive);
        answer.Body.GetProperty("expires_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        var audits = await AuditRowsAsync(world, "artifact.download", seeded.Id.ToString("D"));
        var row = audits.ShouldHaveSingleItem();
        row.ActorId.ShouldBe(engineer.ToString("D"));
        row.Outcome.ShouldBe("sensitive_granted");
        row.RunId.ShouldBe(world.RunId.ToString("D"), "the grant names the run the issued artifact belongs to");
    }

    /// <summary>
    /// A caller without <c>artifact:read</c> is refused before any row is read: the existing and the unknown id
    /// answer the identical 403, so the route is no oracle, and neither denial writes an audit row.
    /// </summary>
    [Fact]
    public async Task DownloadUrl_WithoutArtifactRead_SameRefusalForExistingAndUnknown()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var unrelated = await AddMemberAsync(world, roleCode: null);
        var member = world.MemberClient(unrelated);

        var existing = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        var unknown = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{Guid.CreateVersion7():D}/download-url", member.Caller);

        existing.Status.ShouldBe(HttpStatusCode.Forbidden);
        existing.ErrorCode.ShouldBe("forbidden");
        unknown.Status.ShouldBe(existing.Status);
        unknown.ErrorCode.ShouldBe(existing.ErrorCode);
        unknown.ErrorMessage.ShouldBe(existing.ErrorMessage);
        (await AuditRowsAsync(world, "artifact.download_denied")).ShouldBeEmpty(
            "a principal without artifact:read must not be able to probe for rows");
    }

    /// <summary>Only a <c>ready</c> row has bytes at its final key: a non-ready row is a 409, not a URL.</summary>
    [Fact]
    public async Task DownloadUrl_NonReadyArtifact_Is409()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(
            world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean", state: ArtifactState.Uploaded);

        var member = world.MemberClient(world.Founder);

        var answer = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("artifact_state_conflict");
        answer.Body.TryGetProperty("download_url", out _).ShouldBeFalse();
        (await AuditRowsAsync(world, "artifact.download", seeded.Id.ToString("D"))).ShouldBeEmpty(
            "nothing was issued, so nothing is recorded as granted");
    }

    /// <summary>
    /// A row being deleted or already deleted gates its content for every caller, so the lifecycle answer must win
    /// over the sensitivity one: both the viewer and the caller holding <c>artifact:read_sensitive</c> are answered
    /// the same 409 rather than a 403 or a URL, and neither writes a grant row.
    /// </summary>
    /// <param name="state">The lifecycle state under test: the soft delete and its terminal form.</param>
    [Theory]
    [InlineData(ArtifactState.PendingDelete)]
    [InlineData(ArtifactState.Deleted)]
    public async Task DownloadUrl_DeletedOrDeletingArtifact_Is409ForBothCallers(string state)
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(
            world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean", state: state);
        var viewer = world.MemberClient(await AddMemberAsync(world, "viewer"));
        var engineer = world.MemberClient(await AddMemberAsync(world, "qa_engineer"));

        foreach (var member in new[] { viewer, engineer })
        {
            var answer = await member.Client.SendAsync(
                HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);

            answer.Status.ShouldBe(HttpStatusCode.Conflict, "the bytes are gone or going, whatever the permission");
            answer.ErrorCode.ShouldBe("artifact_state_conflict");
            answer.Body.TryGetProperty("download_url", out _).ShouldBeFalse();
        }

        (await AuditRowsAsync(world, "artifact.download", seeded.Id.ToString("D"))).ShouldBeEmpty();
        (await AuditRowsAsync(world, "artifact.download_denied", seeded.Id.ToString("D"))).ShouldBeEmpty();
    }

    /// <summary>The metadata and download answers carry per-caller, credential-bearing content, so neither may be
    /// stored by any cache — the 200s and the refused ones alike.</summary>
    [Fact]
    public async Task GetArtifact_AndDownloadUrl_AnswersCacheControlNoStore()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = world.MemberClient(await AddMemberAsync(world, "viewer"));
        var engineer = world.MemberClient(await AddMemberAsync(world, "qa_engineer"));

        var metadata = await viewer.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", viewer.Caller);
        var denied = await viewer.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", viewer.Caller);
        var issued = await engineer.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", engineer.Caller);

        metadata.Status.ShouldBe(HttpStatusCode.OK);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        issued.Status.ShouldBe(HttpStatusCode.OK);

        metadata.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        denied.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        issued.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    /// <summary>No gated or denied answer carries the storage key, the content address or a presigned URL.</summary>
    [Fact]
    public async Task GatedAndDenied_Bodies_CarryNoStorageValues()
    {
        await using var world = await StartAsync();
        var seeded = await SeedArtifactAsync(world, ArtifactKind.Trace, ArtifactSensitivity.Sensitive, "clean");
        var viewer = await AddMemberAsync(world, "viewer");
        var member = world.MemberClient(viewer);

        var metadata = await member.Client.SendAsync(HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}", member.Caller);
        metadata.Status.ShouldBe(HttpStatusCode.OK);
        var metadataBody = metadata.Body.GetRawText();
        metadataBody.ShouldNotContain("object_key", Case.Sensitive);
        metadataBody.ShouldNotContain("checksum", Case.Sensitive);
        metadataBody.ShouldNotContain("download_url", Case.Sensitive);
        metadataBody.ShouldNotContain(seeded.ObjectKey, Case.Sensitive);
        metadataBody.ShouldNotContain(Checksum, Case.Sensitive);

        var denied = await member.Client.SendAsync(
            HttpMethod.Get, $"/v1/artifacts/{seeded.Id:D}/download-url", member.Caller);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        var deniedBody = denied.Body.GetRawText();
        deniedBody.ShouldNotContain("download_url", Case.Sensitive);
        deniedBody.ShouldNotContain("X-Amz-Signature", Case.Sensitive);
        deniedBody.ShouldNotContain(seeded.ObjectKey, Case.Sensitive);
        deniedBody.ShouldNotContain(Checksum, Case.Sensitive);
    }
}

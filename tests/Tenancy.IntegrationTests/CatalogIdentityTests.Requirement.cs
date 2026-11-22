using System.Net;
using System.Text.Json;
using Evidgate.Modules.Catalog.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogIdentityTests
{
    // ------------------------------------------------------------------ requirement pointer (03.1.4)

    [Fact]
    public async Task RequirementRef_IsNullByDefault_IsTrimmed_AndRejectsOver128Chars()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        var initial = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin);
        initial.Status.ShouldBe(HttpStatusCode.OK);
        initial.Body.GetProperty("case").GetProperty("requirement_ref").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBeNull();

        var padded = await PatchRequirementAsync(world, caseId, new { requirement_ref = "  REQ-1042  " });
        padded.Status.ShouldBe(HttpStatusCode.OK);
        padded.Body.GetProperty("requirement_ref").GetString().ShouldBe("REQ-1042");
        padded.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBe("REQ-1042");

        // 128 characters is the limit (D-A6), and it is measured after trimming.
        var longest = await PatchRequirementAsync(world, caseId, new { requirement_ref = " " + new string('r', 128) + " " });
        longest.Status.ShouldBe(HttpStatusCode.OK);
        longest.Body.GetProperty("requirement_ref").GetString().ShouldBe(new string('r', 128));

        // A full requirement URL survives the character set, but a query string does not: '?' is not a member.
        var url = await PatchRequirementAsync(
            world, caseId, new { requirement_ref = "https://jira.example.com/browse/PROJ-1#criteria" });
        url.Status.ShouldBe(HttpStatusCode.OK);
        url.Body.GetProperty("requirement_ref").GetString().ShouldBe("https://jira.example.com/browse/PROJ-1#criteria");

        var tooLong = await PatchRequirementAsync(world, caseId, new { requirement_ref = new string('r', 129) });
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The envelope's top-level code is the shared 422 `request_invalid` (00 §9.2); the field-level verdict lives
        // in `details[]`, exactly as every other validation failure in this API reports itself.
        tooLong.ErrorCode.ShouldBe("request_invalid");
        var tooLongDetail = tooLong.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        tooLongDetail.GetProperty("path").GetString().ShouldBe("requirement_ref");
        tooLongDetail.GetProperty("reason").GetString().ShouldBe("invalid_length");

        // The refused value changed nothing, so the accepted one is still stored.
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case")))
            .ShouldBe("https://jira.example.com/browse/PROJ-1#criteria");
        (await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case"))).ShouldBe(4);
    }

    [Theory]
    // Blank trims to nothing, so it is a length refusal; every other row holds a character the set does not carry
    // (or, for a lone surrogate, not text at all), which is a value refusal.
    [InlineData("   ", "invalid_length")]
    [InlineData("REQ 1042", "invalid_value")]
    [InlineData("https://jira.example.com/browse/PROJ-1?focused=1", "invalid_value")]
    [InlineData("REQ\u0001", "invalid_value")]
    [InlineData("REQ\u200b", "invalid_value")]
    [InlineData("REQ\n1042", "invalid_value")]
    [InlineData("\tREQ-1", "invalid_value")]
    [InlineData("REQ-1\n", "invalid_value")]
    [InlineData("\uD800", "invalid_value")]
    [InlineData("REQ\uD800", "invalid_value")]
    public async Task RequirementRef_RefusesBlankCharsetAndControlBearingValues(string value, string reason)
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        var answer = await PatchRequirementAsync(world, caseId, new { requirement_ref = value });

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single()
            .GetProperty("reason").GetString().ShouldBe(reason);
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBeNull();
        (await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case"))).ShouldBe(1);
    }

    [Fact]
    public async Task RequirementRef_RefusesAbsentNonNullStringAndExtraMembers()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));

        var absent = await PatchRequirementAsync(world, caseId, new { });
        absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        absent.ErrorCode.ShouldBe("request_invalid");
        absent.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Single().GetProperty("reason").GetString().ShouldBe("required");

        var notAString = await PatchRequirementAsync(world, caseId, new { requirement_ref = 42 });
        notAString.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        notAString.ErrorCode.ShouldBe("request_invalid");

        var extra = await PatchRequirementAsync(world, caseId, new { requirement_ref = "REQ-1", name = "sneaky" });
        extra.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        extra.ErrorCode.ShouldBe("request_invalid");
        extra.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Single().GetProperty("path").GetString().ShouldBe("name");

        // The name the extra member tried to change is untouched: this route writes the pointer only.
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select name from public.test_case"))).ShouldBe("a.spec");
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBeNull();
    }

    [Fact]
    public async Task RequirementRefChange_IsMetadataOnly_NoRevisionNoHashChange()
    {
        await using var world = await StartAsync();
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        var caseId = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));
        var before = await _fixture.ScalarAsync<string>(
            world.Database,
            Scoped(world, "select string_agg(revision_hash || '|' || assertion_hash || '|' || content_sha256, ',' order by created_at) from public.test_revision"));
        var revisions = await CountAsync(world, "test_revision");
        revisions.ShouldBeGreaterThan(0);

        var set = await PatchRequirementAsync(world, caseId, new { requirement_ref = "REQ-1042" });
        set.Status.ShouldBe(HttpStatusCode.OK);

        // Replaying the same value is a no-op, and clearing it is another metadata write.
        (await PatchRequirementAsync(world, caseId, new { requirement_ref = "REQ-1042" })).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case"))).ShouldBe(2);
        var cleared = await PatchRequirementAsync(world, caseId, new { requirement_ref = (string?)null });
        cleared.Status.ShouldBe(HttpStatusCode.OK);
        cleared.Body.GetProperty("requirement_ref").ValueKind.ShouldBe(JsonValueKind.Null);

        (await CountAsync(world, "test_revision")).ShouldBe(revisions);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            Scoped(world, "select string_agg(revision_hash || '|' || assertion_hash || '|' || content_sha256, ',' order by created_at) from public.test_revision")))
            .ShouldBe(before);
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBeNull();
    }

    [Fact]
    public async Task RequirementRefPatch_NeedsCaseWrite_ForeignProjectAndOrgAre404()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, "returns");
        await RegisterAsync(world, Commit1, Annotated("a.spec.ts", IdA));
        await RegisterAsync(world.Api, otherOrg, otherProject, Commit1, Annotated("a.spec.ts", IdA));
        var mine = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(world, "select id from public.test_case"));
        var theirs = await _fixture.ScalarAsync<Guid>(world.Database, Scoped(otherOrg, "select id from public.test_case"));

        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{mine}", stranger, new { requirement_ref = "REQ-1" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{theirs}", world.Admin, new { requirement_ref = "REQ-1" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);

        // Same org, other project: the route's project scopes the case, so this is a 404 and never a write.
        var wrongProject = await world.Client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{siblingProject}/cases/{mine}", world.Admin, new { requirement_ref = "REQ-1" }, NewKey());
        wrongProject.Status.ShouldBe(HttpStatusCode.NotFound);
        wrongProject.ErrorCode.ShouldBe("not_found");

        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case", $"id = '{mine}'"))).ShouldBeNull();
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(otherOrg, "select requirement_ref from public.test_case", $"id = '{theirs}'"))).ShouldBeNull();
        (await _fixture.ScalarAsync<int>(world.Database, Scoped(world, "select version from public.test_case", $"id = '{mine}'"))).ShouldBe(1);

        // The owner still can, and the other org's row stays invisible to every read of this tenant.
        var stored = await PatchRequirementAsync(world, mine, new { requirement_ref = "REQ-1" });
        stored.Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{theirs}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(otherOrg, "select requirement_ref from public.test_case", $"id = '{theirs}'")))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Register_SidecarRequirement_IsStoredOnce_AndALaterSyncNeverRewritesIt()
    {
        await using var world = await StartAsync();
        SourceFile Sidecar(string? requirement) =>
            new(
                "tests/evidgate.cases.yaml",
                "version: 1\ncases:\n"
                + $"  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n"
                + (requirement is null ? string.Empty : $"    requirement: {requirement}\n"));

        var source = new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript");
        await RegisterAsync(world, Commit1, source, Sidecar("\" REQ-1042 \""));

        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBe("REQ-1042");

        // A later sync with another value, and then with none, leaves the stored pointer alone: after
        // registration the explicit route is its only writer, so a sidecar edit cannot silently re-point a case.
        await RegisterAsync(world, Commit2, source, Sidecar("REQ-9999"));
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBe("REQ-1042");

        await RegisterAsync(world, Commit3, source, Sidecar(null));
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBe("REQ-1042");
        (await CountAsync(world, "test_case")).ShouldBe(1);
    }

    [Fact]
    public async Task Register_SidecarBooleanRequirement_IsAWarning_AndTheCaseStillRegisters()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    requirement: true\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        // A bare true/false is a scalar the schema accepts but no requirement id: warning, no conflict, id kept.
        // The report carries both entries for this one file: the warning about its dropped pointer and the
        // registration that still gave it its id.
        report.Warnings.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Registered.ShouldBe(1);
        report.Entries.Single(entry => entry.Result == CaseSyncResults.Warning).Code.ShouldBe(CaseSyncCodes.InvalidSidecarEntry);
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBeNull();
        (await CountAsync(world, "test_case")).ShouldBe(1);
    }

    [Fact]
    public async Task Register_SidecarNumericRequirement_IsStoredAsItsDigits()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    requirement: 1042\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        report.Warnings.ShouldBe(0);
        report.Registered.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case"))).ShouldBe("1042");
    }

    [Theory]
    [InlineData("REQ 1042")]
    [InlineData("REQ?focused=1")]
    [InlineData("   ")]
    [InlineData("REQ-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Register_SidecarRequirementOutsideTheRule_WarnsOnThatEntry_AndBothCasesAreWritten(string requirement)
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n"
            + $"  - file: good.spec.ts\n    evidgate.id: {IdA}\n    requirement: REQ-1042\n"
            + $"  - file: bad.spec.ts\n    evidgate.id: {IdB}\n    requirement: {JsonSerializer.Serialize(requirement)}\n");

        var report = await RegisterAsync(
            world,
            Commit1,
            new SourceFile("tests/good.spec.ts", "test('x')", "playwright", "typescript"),
            new SourceFile("tests/bad.spec.ts", "test('y')", "playwright", "typescript"),
            sidecar);

        // One unusable pointer is metadata about one entry: it warns there and neither case loses its id, because
        // document validation runs before per-entry semantics and keeps the value's shape unconstrained.
        report.Warnings.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Registered.ShouldBe(2);
        report.Entries.Single(entry => entry.Result == CaseSyncResults.Warning).Path.ShouldBe("tests/bad.spec.ts");
        (await CountAsync(world, "test_case")).ShouldBe(2);
        (await _fixture.ScalarAsync<string>(world.Database, Scoped(world, "select requirement_ref from public.test_case", "file_path = 'tests/good.spec.ts'"))).ShouldBe("REQ-1042");
        (await _fixture.ScalarAsync<string?>(world.Database, Scoped(world, "select requirement_ref from public.test_case", "file_path = 'tests/bad.spec.ts'"))).ShouldBeNull();
    }

    [Fact]
    public async Task Register_SidecarRequirementInMalformedManifest_IsAWarning_AndNoCaseIsWritten()
    {
        await using var world = await StartAsync();
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {IdA}\n    requirement: \"REQ-1\"\n  - file: [\n");

        var report = await RegisterAsync(world, Commit1, new SourceFile("tests/checkout.spec.ts", "test('x')", "playwright", "typescript"), sidecar);

        report.Warnings.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Registered.ShouldBe(0);
        (await CountAsync(world, "test_case")).ShouldBe(0);
        var answer = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{world.Project}/cases", world.Admin);
        answer.Status.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<ApiAnswer> PatchRequirementAsync(World world, Guid caseId, object body) =>
        world.Client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{caseId}", world.Admin, body, NewKey());
}

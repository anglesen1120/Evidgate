using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReportComparisonTests
{
    [Fact]
    public async Task Comparison_ForeignReportReadableByItsOwner_IsNotFoundUnderAnotherOrg()
    {
        await using var world = await StartAsync();
        var localRun = await SeedRunAsync(world);
        var (org, project, run) = await CreateOtherTenantAsync(world);
        var report = await SeedReportAsync(world, run, org: org, project: project,
            events: [new EventSeed(1, "foreign-case", "passed")]);

        var owner = await GetAsync(world with { Org = org, Project = project }, run, report.Id);
        owner.Status.ShouldBe(HttpStatusCode.OK, owner.ErrorMessage);
        Cases(owner).ShouldHaveSingleItem();

        var denied = await GetAsync(world, localRun, report.Id);
        var unknown = await GetAsync(world, localRun, Guid.CreateVersion7());
        denied.Status.ShouldBe(HttpStatusCode.NotFound, denied.ErrorMessage);
        denied.ErrorCode.ShouldBe("not_found");
        denied.ErrorCode.ShouldBe(unknown.ErrorCode);
        denied.ErrorMessage.ShouldBe(unknown.ErrorMessage);
        denied.Body.TryGetProperty("cases", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Comparison_ExistingReport_OnlyItsOwnRunCanReadIt()
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var otherRun = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run, events: [new EventSeed(1, "own-run", "passed")]);

        var allowed = await GetAsync(world, run, report.Id);
        allowed.Status.ShouldBe(HttpStatusCode.OK, allowed.ErrorMessage);
        allowed.Body.GetProperty("run_id").GetGuid().ShouldBe(run);

        var denied = await GetAsync(world, otherRun, report.Id);
        denied.Status.ShouldBe(HttpStatusCode.NotFound, denied.ErrorMessage);
        denied.ErrorCode.ShouldBe("not_found");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Comparison_NonUuidRouteSegment_IsNotFound(bool invalidRun)
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run);
        (await GetAsync(world, run, report.Id)).Status.ShouldBe(HttpStatusCode.OK);
        var runSegment = invalidRun ? "not-a-uuid" : run.ToString("D", CultureInfo.InvariantCulture);
        var reportSegment = invalidRun ? report.Id.ToString("D", CultureInfo.InvariantCulture) : "not-a-uuid";

        var answer = await world.Client.SendAsync(HttpMethod.Get,
            $"/v1/runs/{runSegment}/reports/{reportSegment}/comparison", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
    }

    [Theory]
    [InlineData("malformed", "malformed")]
    [InlineData("malformed", "parse_timeout")]
    [InlineData("unsupported", "producer_unknown")]
    public async Task Comparison_RefusedReport_DoesNotServeEventsOfAnEarlierParse(string outcome, string reason)
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var mapped = await SeedReportAsync(world, run, events: [new EventSeed(1, "mapped", "passed")]);
        Cases(await GetAsync(world, run, mapped.Id)).ShouldHaveSingleItem();
        var report = await SeedReportAsync(world, run, outcome: outcome, reason: reason, observedTests: 0,
            events: [new EventSeed(1, "earlier-parse", "failed")]);

        var answer = await GetAsync(world, run, report.Id, limit: 1, cursor: "0");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Cases(answer).ShouldBeEmpty();
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
        answer.Body.GetProperty("reason").GetString().ShouldBe(reason);
        answer.Body.GetProperty("raw_report_artifact_id").GetGuid().ShouldBe(report.ArtifactId);
        var summary = answer.Body.GetProperty("summary");
        summary.GetProperty("status").GetString().ShouldBe(outcome);
        summary.GetProperty("observed_tests").GetInt32().ShouldBe(0);
        if (outcome == "malformed")
        {
            Codes(answer).ShouldHaveSingleItem().ShouldBe("malformed");
            answer.Body.GetProperty("parser_id").GetString().ShouldBe(ParserId);
        }
        else
        {
            Codes(answer).ShouldBeEmpty();
            answer.Body.GetProperty("parser_id").ValueKind.ShouldBe(JsonValueKind.Null);
            answer.Body.GetProperty("parser_version").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Comparison_DeclaredZeroWithNoCases_RemainsMappedWithoutDiscrepancies()
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run, declaredTests: 0, observedTests: 0);

        var answer = await GetAsync(world, run, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Cases(answer).ShouldBeEmpty();
        Codes(answer).ShouldBeEmpty();
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
        var summary = answer.Body.GetProperty("summary");
        summary.GetProperty("status").GetString().ShouldBe("mapped");
        summary.GetProperty("declared_tests").GetInt32().ShouldBe(0);
        summary.GetProperty("declared_failures").ValueKind.ShouldBe(JsonValueKind.Null);
        summary.GetProperty("observed_tests").GetInt32().ShouldBe(0);
        summary.GetProperty("observed_failures").GetInt32().ShouldBe(0);
        summary.GetProperty("observed_errors").GetInt32().ShouldBe(0);
        summary.GetProperty("observed_skipped").GetInt32().ShouldBe(0);
        answer.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Comparison_ViewerScopedToSiblingProject_IsNotFoundWhileMatchingProjectSucceeds()
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run, events: [new EventSeed(1, "member-case", "passed")]);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("comparison-sibling"));
        var viewer = await AddMemberAsync(_fixture, world.Database, world.Org, "viewer");
        var caller = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        await using var matchingHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project, RequestPrincipal.Member(viewer));
        using var matching = matchingHost.CreateClient();
        var allowed = await matching.SendAsync(HttpMethod.Get, ComparisonUrl(run, report.Id), caller);
        allowed.Status.ShouldBe(HttpStatusCode.OK, allowed.ErrorMessage);
        Cases(allowed).ShouldHaveSingleItem();
        await using var siblingHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, sibling, RequestPrincipal.Member(viewer));
        using var narrowed = siblingHost.CreateClient();

        var denied = await narrowed.SendAsync(HttpMethod.Get, ComparisonUrl(run, report.Id), caller);
        var unknown = await narrowed.SendAsync(HttpMethod.Get, ComparisonUrl(run, Guid.CreateVersion7()), caller);

        denied.Status.ShouldBe(HttpStatusCode.NotFound, denied.ErrorMessage);
        denied.ErrorCode.ShouldBe("not_found");
        denied.ErrorMessage.ShouldBe(unknown.ErrorMessage);
    }

    [Theory]
    [InlineData("abc", HttpStatusCode.BadRequest)]
    [InlineData("1.5", HttpStatusCode.BadRequest)]
    [InlineData(" 5", HttpStatusCode.BadRequest)]
    [InlineData("+5", HttpStatusCode.BadRequest)]
    [InlineData("01", HttpStatusCode.BadRequest)]
    [InlineData("-1", HttpStatusCode.UnprocessableEntity)]
    [InlineData("2147483648", HttpStatusCode.UnprocessableEntity)]
    [InlineData("-2147483649", HttpStatusCode.UnprocessableEntity)]
    [InlineData("9223372036854775807", HttpStatusCode.UnprocessableEntity)]
    [InlineData("9223372036854775808", HttpStatusCode.UnprocessableEntity)]
    [InlineData("-9223372036854775808", HttpStatusCode.UnprocessableEntity)]
    [InlineData("-9223372036854775809", HttpStatusCode.UnprocessableEntity)]
    public async Task Comparison_LimitText_DistinguishesMalformedFromOutOfRange(string limit, HttpStatusCode expected)
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run);
        var valid = await GetAsync(world, run, report.Id, limit: 1);
        valid.Status.ShouldBe(HttpStatusCode.OK, valid.ErrorMessage);

        var answer = await world.Client.SendAsync(HttpMethod.Get,
            ComparisonUrl(run, report.Id) + "?limit=" + Uri.EscapeDataString(limit), world.Admin);

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        FieldPath(answer).ShouldBe("limit");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9223372036854775807")]
    public async Task Comparison_CanonicalCursorBoundary_ReturnsOnlyEventsAfterIt(string cursor)
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run, events: [new EventSeed(1, "boundary", "passed")]);
        (await GetAsync(world, run, report.Id)).Status.ShouldBe(HttpStatusCode.OK);

        var answer = await GetAsync(world, run, report.Id, cursor: cursor);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Cases(answer).Length.ShouldBe(cursor == "0" ? 1 : 0);
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("+1")]
    [InlineData(" 1")]
    public async Task Comparison_InvalidCursorBoundary_IsUnprocessable(string cursor)
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run);
        (await GetAsync(world, run, report.Id, cursor: "0")).Status.ShouldBe(HttpStatusCode.OK);

        var answer = await GetAsync(world, run, report.Id, cursor: cursor);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        FieldPath(answer).ShouldBe("cursor");
    }

    [Fact]
    public async Task Comparison_DefaultPageSize_IsFiftyAndEmptyCursorKeepsFirstPage()
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run,
            events: [.. Enumerable.Range(1, 51).Select(sequence => new EventSeed(sequence, $"default-{sequence}", "passed"))]);

        var first = await GetAsync(world, run, report.Id, cursor: string.Empty);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        Cases(first).Length.ShouldBe(50);
        first.Body.GetProperty("next_cursor").GetString().ShouldBe("50");
        var last = await GetAsync(world, run, report.Id, cursor: "50");
        last.Status.ShouldBe(HttpStatusCode.OK, last.ErrorMessage);
        Cases(last).ShouldHaveSingleItem().GetProperty("case_ref").GetString().ShouldBe(ExecutionKeyFor("default-51"));
        last.Body.GetProperty("summary").GetRawText().ShouldBe(first.Body.GetProperty("summary").GetRawText());
        last.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Comparison_RepeatedCaseSpanningPageBoundary_IsCountedOnceWithoutReturningAgain()
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run, events:
        [
            new EventSeed(1, "repeated", "failed"),
            new EventSeed(2, "other", "passed"),
            new EventSeed(3, "repeated", "passed"),
            new EventSeed(4, "last", "skipped"),
        ]);

        var first = await GetAsync(world, run, report.Id, limit: 1);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        var item = Cases(first).ShouldHaveSingleItem();
        item.GetProperty("observed_events").GetInt32().ShouldBe(2);
        item.GetProperty("declared_status").GetString().ShouldBe("failed");
        first.Body.GetProperty("next_cursor").GetString().ShouldBe("1");
        var second = await GetAsync(world, run, report.Id, limit: 1, cursor: "1");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        Cases(second).ShouldHaveSingleItem().GetProperty("case_ref").GetString().ShouldBe(ExecutionKeyFor("other"));
        second.Body.GetProperty("next_cursor").GetString().ShouldBe("2");
        var third = await GetAsync(world, run, report.Id, limit: 1, cursor: "2");
        third.Status.ShouldBe(HttpStatusCode.OK, third.ErrorMessage);
        Cases(third).ShouldHaveSingleItem().GetProperty("case_ref").GetString().ShouldBe(ExecutionKeyFor("last"));
        third.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Comparison_RepeatedStoredCodes_AreDistinctAndCanonicallyOrdered()
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run, outcome: "partial",
            discrepancies: """
            [{"code":"status_mismatch","count":2},{"code":"duplicate_case","count":1},
             {"code":"unmapped_case","count":1},{"code":"count_mismatch","count":3},
             {"code":"status_mismatch","count":1}]
            """);

        var answer = await GetAsync(world, run, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        string.Join(",", Codes(answer)).ShouldBe("count_mismatch,unmapped_case,duplicate_case,status_mismatch");
    }

    [Theory]
    [InlineData("limit=1&limit=2", HttpStatusCode.BadRequest)]
    [InlineData("cursor=1&cursor=2", HttpStatusCode.UnprocessableEntity)]
    public async Task Comparison_DuplicateQueryValues_AreRejected(string query, HttpStatusCode expected)
    {
        await using var world = await StartAsync();
        var run = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, run);
        (await GetAsync(world, run, report.Id, limit: 1, cursor: "0")).Status.ShouldBe(HttpStatusCode.OK);

        var answer = await world.Client.SendAsync(HttpMethod.Get, ComparisonUrl(run, report.Id) + "?" + query, world.Admin);

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        FieldPath(answer).ShouldBe(query.StartsWith("limit", StringComparison.Ordinal) ? "limit" : "cursor");
    }
}

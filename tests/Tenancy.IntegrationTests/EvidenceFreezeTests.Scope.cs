using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceFreezeTests
{
    // ------------------------------------------------------------------ tenant isolation + scope guard

    [Fact]
    public async Task Freeze_OrgIsolation()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, Unique.Slug("rival"));

        var ownPlan = await SeedPlanAsync(world);
        var otherPlan = await SeedPlanAsync(world, 2, otherOrg);
        var ownKey = ownPlan.Executions[0].ExecutionKey;
        var otherKey = otherPlan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, ownPlan.RunId, new CaseAttemptInput(ownKey, 1, "passed"));
        await SeedAttemptsAsync(world, otherOrg, otherPlan.RunId, new CaseAttemptInput(otherKey, 1, "passed"));

        var ownArtifact = Guid.CreateVersion7();
        var otherBound = Guid.CreateVersion7();
        var otherOrphan = Guid.CreateVersion7();
        await InsertArtifactAsync(world, ownPlan.RunId, new ArtifactSeed(ownArtifact, ownKey, 1, ArtifactKind.Trace));
        await InsertArtifactsAsync(
            world,
            otherOrg,
            otherProject,
            otherPlan.RunId,
            [
                new ArtifactSeed(otherBound, otherKey, 1, ArtifactKind.Trace),
                new ArtifactSeed(otherOrphan, otherKey, 2, ArtifactKind.Trace),
            ]);

        // Another org's run: nothing is visible, so every declared requirement is missing — never complete.
        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var foreign = await scope.RunAsync(
                otherPlan.RunId,
                new RequiredArtifactSpec(otherKey, ArtifactKind.Trace, FreezeCodes.ArtifactMissing));

            foreign.Ready.ShouldBeEmpty();
            foreign.Orphans.ShouldBeEmpty();
            foreign.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Requirement,
                otherKey,
                ArtifactKind.Trace,
                null,
                null));
            foreign.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));

            await scope.CommitAsync();
        }

        // The org's own run keeps its own artifacts only.
        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var own = await scope.RunAsync(ownPlan.RunId);

            own.Ready.ShouldHaveSingleItem().ArtifactId.ShouldBe(ownArtifact);
            own.Orphans.ShouldBeEmpty();
            own.Missing.ShouldBeEmpty();

            await scope.CommitAsync();
        }

        // The other org's rows were never touched (their own freeze has not run yet).
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{otherOrg}' and attempt_id is not null"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{otherOrg}'"))
            .ShouldBe(2);

        // No ambient tenant scope: refused before any statement.
        await using var services = world.Api.Services.CreateAsyncScope();
        var unscoped = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        world.Api.Statements.Clear();

        await Should.ThrowAsync<InvalidOperationException>(() => unscoped.FreezeAsync(
            new FreezeRequest(ownPlan.RunId, []),
            TestContext.Current.CancellationToken));

        Statements(world).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ project visibility inside one org

    [Fact]
    public async Task Freeze_OtherProjectOfSameOrg_YieldsNoEvidence()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        // A second project of the SAME org: row level security is org-only (0028), so the `a.project_id` predicate
        // of both statements is the only thing that keeps another project's evidence out of a project-scoped freeze.
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "second");
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var artifact = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(artifact, key, 1, ArtifactKind.Trace));

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org, otherProject))
        {
            world.Api.Statements.Clear();
            var result = await scope.RunAsync(
                plan.RunId,
                new RequiredArtifactSpec(key, ArtifactKind.Trace, FreezeCodes.ArtifactMissing));

            result.Ready.ShouldBeEmpty();
            result.Orphans.ShouldBeEmpty();
            result.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));
            result.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Requirement,
                key,
                ArtifactKind.Trace,
                null,
                null));

            // No row was even a candidate, so the call issued the read only and never resolved the attempt.
            Statements(world).Count.ShouldBe(1);
            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(artifact)).ShouldBeNull();

            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where id = '{artifact}' and attempt_id is null"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ authz: no HTTP surface

    [Fact]
    public async Task Freeze_AddsNoHttpRoute()
    {
        await using var world = await StartAsync();

        // The freeze is an internal cross-module service call that 05's
        // reconcile handler makes inside its own tenant transaction — it exposes no /v1 route, so no permission
        // gates it and no caller can freeze evidence over HTTP.
        var routes = world.Api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotBeEmpty();
        routes.ShouldNotContain(
            route => route.Contains("freeze", StringComparison.OrdinalIgnoreCase),
            "IEvidenceFreeze is a service call, never a route");
    }

    // ------------------------------------------------------------------ failure path: request shape

    [Theory]
    [InlineData("request_null")]
    [InlineData("element_null")]
    [InlineData("run_id_empty")]
    [InlineData("too_many_specs")]
    [InlineData("execution_key_short")]
    [InlineData("execution_key_upper")]
    [InlineData("execution_key_non_hex")]
    [InlineData("kind_unknown")]
    [InlineData("reason_code_upper")]
    [InlineData("reason_code_empty")]
    [InlineData("reason_code_too_long")]
    [InlineData("attempt_no_zero")]
    [InlineData("attempt_no_negative")]
    public async Task Freeze_InvalidRequest_Refused_ZeroStatements(string caseName)
    {
        await using var world = await StartAsync();
        var runId = Guid.CreateVersion7();
        var key = new string('a', 64);

        await using var services = world.Api.Services.CreateAsyncScope();
        var freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        world.Api.Statements.Clear();

        // No tenant scope is open on purpose: validation runs before the scope guard and before any statement,
        // so a malformed request from a caller bug never reaches the guard.
        FreezeRequest? request = caseName switch
        {
            "request_null" => null,
            "element_null" => new FreezeRequest(runId, [null!]),
            _ => InvalidRequest(caseName, runId, key),
        };
        var expected = caseName is "request_null" or "element_null"
            ? typeof(ArgumentNullException)
            : typeof(ArgumentException);

        var thrown = await Should.ThrowAsync<ArgumentException>(() => freeze.FreezeAsync(
            request!,
            TestContext.Current.CancellationToken));

        thrown.GetType().ShouldBe(expected);
        Statements(world).ShouldBeEmpty();
    }

    /// <summary>One malformed request of the shape list; a requirement set is otherwise well formed.</summary>
    private static FreezeRequest InvalidRequest(string caseName, Guid runId, string key) => caseName switch
    {
        "run_id_empty" => new FreezeRequest(Guid.Empty, []),
        "too_many_specs" => new FreezeRequest(
            runId,
            Enumerable
                .Range(0, FreezeRequest.MaxRequired + 1)
                .Select(_ => new RequiredArtifactSpec(key, ArtifactKind.Trace, FreezeCodes.ArtifactMissing))
                .ToList()),
        "execution_key_short" => Requirement(runId, new string('a', 63), ArtifactKind.Trace),
        "execution_key_upper" => Requirement(runId, new string('A', 64), ArtifactKind.Trace),
        "execution_key_non_hex" => Requirement(runId, new string('g', 64), ArtifactKind.Trace),
        "kind_unknown" => Requirement(runId, key, "executable"),
        "reason_code_upper" => Requirement(runId, key, ArtifactKind.Trace, "Artifact_missing"),
        "reason_code_empty" => Requirement(runId, key, ArtifactKind.Trace, string.Empty),
        "reason_code_too_long" => Requirement(runId, key, ArtifactKind.Trace, "a" + new string('b', 64)),
        "attempt_no_zero" => Requirement(runId, key, ArtifactKind.Trace, attemptNo: 0),
        "attempt_no_negative" => Requirement(runId, key, ArtifactKind.Trace, attemptNo: -1),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "unknown validation case"),
    };

    /// <summary>One well-formed request carrying a single (here malformed) requirement.</summary>
    private static FreezeRequest Requirement(
        Guid runId,
        string key,
        string kind,
        string reasonCode = FreezeCodes.ArtifactMissing,
        int? attemptNo = null) =>
        new(runId, [new RequiredArtifactSpec(key, kind, reasonCode, attemptNo)]);

    // ------------------------------------------------------------------ integrity: a disagreeing attempt_id

    [Fact]
    public async Task Freeze_StoredAttemptIdDisagreeing_ThrowsAndRewritesNothing()
    {
        await using var world = await StartAsync();
        var ownPlan = await SeedPlanAsync(world);
        var otherPlan = await SeedPlanAsync(world);
        var key = ownPlan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(
            world,
            ownPlan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"));
        await SeedAttemptsAsync(
            world,
            otherPlan.RunId,
            new CaseAttemptInput(otherPlan.Executions[0].ExecutionKey, 1, "passed"));

        var foreignAttemptId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{otherPlan.RunId}'");

        var conflicted = Guid.CreateVersion7();
        var resolvable = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            ownPlan.RunId,
            [
                // 0032's artifact_attempt_fk is (org_id, attempt_id): an attempt of ANOTHER run passes the database.
                new ArtifactSeed(conflicted, key, 1, ArtifactKind.Trace, AttemptId: foreignAttemptId),
                // Would resolve on its own: nothing is rewritten when the batch is refused.
                new ArtifactSeed(resolvable, key, 2, ArtifactKind.Trace),
            ]);

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();

            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(ownPlan.RunId));

            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(conflicted)).ShouldBe(foreignAttemptId);
            (await scope.AttemptIdAsync(resolvable)).ShouldBeNull();

            // The scope is left to roll back, like 05's reconcile on a refusal.
        }

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select attempt_id::text from public.artifact where id = '{conflicted}'"))
            .ShouldBe(foreignAttemptId.ToString());
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where id = '{resolvable}' and attempt_id is null"))
            .ShouldBe(1);

        // The other fail-closed shape of the same clause: a stored attempt_id whose pair resolves to no
        // case_attempt at all (attempt 7 of a planned execution that recorded no such attempt) is a disagreement
        // too — the row is never silently reinterpreted as an orphan.
        var stranded = Guid.CreateVersion7();
        await InsertArtifactAsync(
            world,
            otherPlan.RunId,
            new ArtifactSeed(
                stranded,
                otherPlan.Executions[0].ExecutionKey,
                7,
                ArtifactKind.Trace,
                AttemptId: foreignAttemptId));

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();

            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(otherPlan.RunId));

            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(stranded)).ShouldBe(foreignAttemptId);
        }

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select attempt_id::text from public.artifact where id = '{stranded}'"))
            .ShouldBe(foreignAttemptId.ToString());
    }
}

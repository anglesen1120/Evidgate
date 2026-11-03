using System.Globalization;
using System.Net;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Application.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationTaskTests
{
    private const string LiveLoserMessage = "already exists under a different policy revision or evaluator version";

    [Fact]
    public async Task Evaluate_PayloadOrgMismatch_ThrowsAndWritesNoRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        var payload = new EvidenceRevisionFrozenPayload(
            Guid.CreateVersion7(), seed.Project, seed.RunId, seed.RevisionId, 1);
        var handler = world.Api.Services.GetRequiredService<EvidenceRevisionFrozenTaskHandler>();

        await using (var scope = await OpenScopeAsync(world, seed, TestContext.Current.CancellationToken))
        {
            var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await handler.HandleAsync(payload, scope, TestContext.Current.CancellationToken));
            refusal.Message.ShouldContain("names org");
        }

        (await CountAsync(world, seed)).ShouldBe(0, "a payload that disagrees with its scope is a tamper signal");

        // The positive control: the same payload with the scope's own org stores the verdict.
        await using (var scope = await OpenScopeAsync(world, seed, TestContext.Current.CancellationToken))
        {
            await handler.HandleAsync(
                new EvidenceRevisionFrozenPayload(seed.Org, seed.Project, seed.RunId, seed.RevisionId, 1),
                scope,
                TestContext.Current.CancellationToken);
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync(world, seed)).ShouldBe(1);
        (await StoredRowAsync(world, seed, EvaluationMode.Live)).ShouldNotBeNull().Decision.ShouldBe("PASS");
    }

    [Fact]
    public async Task Evaluate_PayloadOrgCannotOverrideForeignScope_RefusesRunOutsideTenant()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var ownSeed = await SeedAsync(world);
        var foreign = await SeedForeignTenantRunAsync(world);
        var handler = world.Api.Services.GetRequiredService<EvidenceRevisionFrozenTaskHandler>();

        // The payload agrees with the scope's org but names the other tenant's run, which exists in this very database.
        var payload = new EvidenceRevisionFrozenPayload(
            ownSeed.Org, ownSeed.Project, foreign.RunId, foreign.RevisionId, 1);

        await using (var scope = await OpenScopeAsync(world, ownSeed, TestContext.Current.CancellationToken))
        {
            var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await handler.HandleAsync(payload, scope, TestContext.Current.CancellationToken));
            refusal.Message.ShouldContain("no manifest or pinned suite");
        }

        (await _fixture.ScalarAsync<long>(world.Database,
                $"select count(*) from public.run where org_id = '{foreign.Org:D}' and id = '{foreign.RunId:D}'"))
            .ShouldBe(1, "the foreign run is seeded here, so only the tenant decides it is invisible");
        (await _fixture.ScalarAsync<long>(world.Database,
                $"select count(*) from public.evaluation where org_id = '{ownSeed.Org:D}'"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database,
                $"select count(*) from public.evaluation where org_id = '{foreign.Org:D}'"))
            .ShouldBe(0);

        // The positive control: the same handler, in the same database, stores the own tenant's verdict.
        (await EvaluateAsync(world, ownSeed, EvaluationMode.Live, TestContext.Current.CancellationToken))
            .Decision.ShouldBe("PASS");
    }

    [Fact]
    public async Task Evaluate_MissingPolicyPin_RefusesUntilRunIsPinned()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.run set policy_revision_id = null where id = '{seed.RunId:D}'");

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() =>
            EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken));
        refusal.Message.ShouldContain("run pins no policy revision");
        (await CountAsync(world, seed)).ShouldBe(0);

        await _fixture.ExecuteAsync(world.Database,
            $"update public.run set policy_revision_id = '{seed.PolicyRevisionId:D}' where id = '{seed.RunId:D}'");
        var stored = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        stored.Decision.ShouldBe("PASS");
        (await CountAsync(world, seed)).ShouldBe(1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public async Task Evaluate_InvalidRevisionOrMode_RefusesWithoutWriting(int revision, int mode)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        await using (var scope = await OpenScopeAsync(world, seed, TestContext.Current.CancellationToken))
        {
            await using var services = world.Api.Services.CreateAsyncScope();
            var handler = services.ServiceProvider.GetRequiredService<EvaluateRunCommandHandler>();
            var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await handler.Handle(new EvaluateRunCommand(seed.RunId, revision, (EvaluationMode)mode),
                    TestContext.Current.CancellationToken));
            refusal.Message.ShouldContain("no run or no frozen revision");
        }

        (await CountAsync(world, seed)).ShouldBe(0);
        (await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken))
            .Decision.ShouldBe("PASS");
    }

    [Fact]
    public async Task Evaluate_NonexistentRevision_RefusesWithoutWriting()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        await using (var scope = await OpenScopeAsync(world, seed, TestContext.Current.CancellationToken))
        {
            await using var services = world.Api.Services.CreateAsyncScope();
            var handler = services.ServiceProvider.GetRequiredService<EvaluateRunCommandHandler>();
            var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await handler.Handle(new EvaluateRunCommand(seed.RunId, int.MaxValue, EvaluationMode.Live),
                    TestContext.Current.CancellationToken));
            refusal.Message.ShouldContain($"no evidence revision {int.MaxValue}");
        }

        (await CountAsync(world, seed)).ShouldBe(0);
        (await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken))
            .Decision.ShouldBe("PASS");
    }

    [Theory]
    [InlineData("""{"critical_cases":["not-a-uuid"]}""")]
    [InlineData("""{"flaky_budget":"high"}""")]
    public async Task Evaluate_PolicyBodyValueUnreadable_RefusesNamingPolicyRevision(string body)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        var policyId = await RepinToBodyAsync(world, seed, body);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() =>
            EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken));
        refusal.Message.ShouldContain(policyId.ToString("D", CultureInfo.InvariantCulture));
        refusal.Message.ShouldContain("cannot read");
        (await CountAsync(world, seed)).ShouldBe(0);

        // The positive control: a readable body under the same run stores the verdict.
        await RepinToBodyAsync(world, seed, "{}");
        (await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken))
            .Decision.ShouldBe("PASS");
    }

    [Fact]
    public async Task Evaluate_IncompleteRevisionWithoutMissingCode_IsJudgedIncomplete()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        await _fixture.ExecuteAsync(world.Database,
            $"""
            update public.evidence_revision set completeness = 'incomplete', missing = array[]::text[]
             where id = '{seed.RevisionId:D}'
            """);

        var shadow = await EvaluateAsync(world, seed, EvaluationMode.Shadow, TestContext.Current.CancellationToken);
        shadow.Completeness.ShouldBe("incomplete");
        shadow.Decision.ShouldBe("INCONCLUSIVE", "a revision that names no gap is still not complete evidence");
        shadow.Missing.ShouldContain("missing_result");

        // The positive control: the same revision declared complete decides a pass.
        await _fixture.ExecuteAsync(world.Database,
            $"""
            update public.evidence_revision set completeness = 'complete', missing = array[]::text[]
             where id = '{seed.RevisionId:D}'
            """);
        (await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken))
            .Decision.ShouldBe("PASS");
    }

    [Fact]
    public async Task Evaluate_LiveLoserUnderDifferentEvaluatorVersion_RefusesWithoutSecondRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        await InsertLiveEvaluationAsync(world, seed, "eval-foreign-1");

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() =>
            EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken));
        refusal.Message.ShouldContain(LiveLoserMessage);

        (await CountAsync(world, seed)).ShouldBe(1, "the loser stored nothing");
        (await CountAsync(world, seed, "and mode = 'live'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
                world.Database,
                $"select evaluator_version from public.evaluation where org_id = '{seed.Org:D}' and run_id = '{seed.RunId:D}'"))
            .ShouldBe("eval-foreign-1");
    }

    [Fact]
    public async Task Evaluate_RetryOlderRevisionAfterNewAttempt_PreservesFrozenDecision()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);
        var first = await EvaluateThenFailAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        first.Decision.ShouldBe("PASS");
        (await CountAsync(world, seed)).ShouldBe(0);

        await _fixture.ExecuteAsync(world.Database,
            $"""
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select gen_random_uuid(), org_id, run_id, execution_key, case_id, 2, 'failed', 'failed'
              from public.case_attempt where org_id = '{seed.Org:D}' and run_id = '{seed.RunId:D}' and attempt_no = 1;
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values ('{Guid.CreateVersion7():D}', '{seed.Org:D}', '{seed.RunId:D}', 2,
                    'complete', array[]::text[], 'has_failures', 1, '{seed.ManifestHash}');
            """);

        await using (var scope = await OpenScopeAsync(world, seed, TestContext.Current.CancellationToken))
        {
            await using var services = world.Api.Services.CreateAsyncScope();
            var handler = services.ServiceProvider.GetRequiredService<EvaluateRunCommandHandler>();
            var newer = await handler.Handle(new EvaluateRunCommand(seed.RunId, 2, EvaluationMode.Live),
                TestContext.Current.CancellationToken);
            newer.Value.Decision.ShouldBe("FAIL", "the new revision must see the new failing attempts");
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        var retried = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        retried.Decision.ShouldBe(first.Decision, "new evidence belongs to revision 2, not a retry of revision 1");
        retried.Reasons.ShouldBe(first.Reasons);
    }

    /// <summary>
    /// Pins the run to a fresh policy revision carrying <paramref name="body"/> and answers its id, so a body the schema
    /// accepts but the evaluator cannot read is what the next attempt meets.
    /// </summary>
    private async Task<Guid> RepinToBodyAsync(RunCreationTests.World world, Seed seed, string body)
    {
        var policyId = Guid.CreateVersion7();
        var hash = "sha256:"
            + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture)
            + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with next as (
                insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
                select @policy_id, @org_id, @project_id,
                       coalesce((select max(revision_no) from public.policy_revision
                                  where org_id = @org_id and project_id = @project_id), 0) + 1,
                       @hash, @body::jsonb, @created_by
                returning id)
            update public.run set policy_revision_id = (select id from next)
             where org_id = @org_id and id = @run_id
            """;
        command.Parameters.AddWithValue("policy_id", policyId);
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("project_id", seed.Project);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("created_by", Guid.CreateVersion7());
        command.Parameters.AddWithValue("run_id", seed.RunId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return policyId;
    }

    /// <summary>
    /// A second tenant inside the same database: org, project, cases, one suite revision, one environment revision, one
    /// uploaded manifest and one run, then its pinned policy, its attempts and its complete evidence revision.
    /// </summary>
    private async Task<ForeignTenantRun> SeedForeignTenantRunAsync(RunCreationTests.World world)
    {
        var user = Guid.CreateVersion7();
        var slug = Unique.Slug("rival");
        var orgCreated = await world.Client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"),
            new { name = "rival", slug },
            RegistryApi.NewKey());
        orgCreated.Status.ShouldBe(HttpStatusCode.Created, orgCreated.ErrorMessage);
        var org = orgCreated.Id();

        var projectCreated = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Resolved(user: user),
            new { name = "checkout", slug = "checkout" },
            RegistryApi.NewKey());
        projectCreated.Status.ShouldBe(HttpStatusCode.Created, projectCreated.ErrorMessage);
        var project = projectCreated.Id();

        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var cases = await ManifestTests.SeedCasesAsync(_fixture, world.Api, org, project, world.Database, 2);

        var suite = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/suites",
            admin,
            new { name = "Smoke", slug = "smoke" },
            RegistryApi.NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);
        var suiteRevision = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{suite.Id()}/revisions",
            admin,
            new { members = cases.Select(id => new { case_revision_id = id }).ToArray() },
            RegistryApi.NewKey());
        suiteRevision.Status.ShouldBe(HttpStatusCode.Created, suiteRevision.ErrorMessage);

        var environment = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/environments/staging/revisions",
            admin,
            new { base_urls = new { web = "https://staging.example.test" }, dependency_versions = new { postgres = "18.0" } },
            RegistryApi.NewKey());
        environment.Status.ShouldBe(HttpStatusCode.Created, environment.ErrorMessage);

        var manifest = RunCreationTests.Manifest(suiteRevision.Id("suite_revision_id"), cases, variants: 2, shards: 2);
        manifest["environment_revision_id"] = environment.Id("revision_id").ToString();
        var manifestId = await RunCreationTests.UploadAsync(world.Client, admin, project, manifest);

        var created = await RunCreationTests.CreateRunAsync(
            world, RunCreationTests.RunBody(manifestId), admin, project: project);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        var revisionId = await SeedForeignEvidenceAsync(world, org, project, runId);
        return new ForeignTenantRun(org, project, runId, revisionId);
    }

    /// <summary>
    /// Pins a policy revision to the foreign run, gives every planned execution one passing attempt and freezes one
    /// complete evidence revision, so a read that ignored the tenant would find a judgeable run, not an empty one.
    /// </summary>
    private async Task<Guid> SeedForeignEvidenceAsync(
        RunCreationTests.World world,
        Guid org,
        Guid project,
        Guid runId)
    {
        var policyId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var manifestHash = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select manifest_hash from public.manifest where org_id = '{org:D}' and project_id = '{project:D}' "
            + $"and id = (select manifest_id from public.run where org_id = '{org:D}' and id = '{runId:D}')");

        await _fixture.ExecuteAsync(world.Database,
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values ('{policyId:D}', '{org:D}', '{project:D}', 1, 'sha256:{new string('f', 64)}',
                    jsonb_build_object(), '{Guid.CreateVersion7():D}');

            update public.run set policy_revision_id = '{policyId:D}'
             where org_id = '{org:D}' and id = '{runId:D}';

            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select gen_random_uuid(), pe.org_id, pe.run_id, pe.execution_key, tr.case_id, 1, 'passed', 'passed'
              from public.planned_execution pe
              join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
             where pe.org_id = '{org:D}' and pe.run_id = '{runId:D}';

            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values ('{revisionId:D}', '{org:D}', '{runId:D}', 1, 'complete', array[]::text[], 'all_passed', 0,
                    '{manifestHash}');
            """);

        return revisionId;
    }

    /// <summary>Writes the live verdict another evaluator version already stored, so the next attempt's insert loses the live
    /// partial key and its six-column read-back finds nothing.
    /// </summary>
    private async Task InsertLiveEvaluationAsync(
        RunCreationTests.World world,
        Seed seed,
        string evaluatorVersion)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash,
               completeness, missing, outcome, decision, reasons, evaluator_version, mode,
               exceptions_hash, exceptions_as_of)
            values (@id, @org_id, @run_id, 1, @policy_revision_id, @policy_hash, @manifest_hash,
                    'complete', '{}', 'all_passed', 'PASS', '{scope_build_policy_pinned}', @evaluator_version, 'live',
                    @exceptions_hash, now())
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("run_id", seed.RunId);
        command.Parameters.AddWithValue("policy_revision_id", seed.PolicyRevisionId);
        command.Parameters.AddWithValue("policy_hash", seed.PolicyHash);
        command.Parameters.AddWithValue("manifest_hash", seed.ManifestHash);
        command.Parameters.AddWithValue("evaluator_version", evaluatorVersion);
        command.Parameters.AddWithValue("exceptions_hash", "sha256:" + new string('e', 64));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The second tenant's run, pinned policy and complete evidence revision, all inside the world's database.</summary>
    private sealed record ForeignTenantRun(Guid Org, Guid Project, Guid RunId, Guid RevisionId);
}

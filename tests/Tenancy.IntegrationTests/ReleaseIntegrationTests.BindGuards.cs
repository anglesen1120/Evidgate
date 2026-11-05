// The refusals of the explicit bind: a draft release, a run whose live evaluation closed the binding window, a
// release or run of another tenant, a caller without `run:create`, and the cookie session's CSRF gate.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>Seeds one policy revision and one <c>live</c> evaluation of the run, as the migration role.</summary>
    /// <remarks>
    /// The partial unique index on <c>(org_id, run_id, evidence_revision) where mode = 'live'</c> is the guard's
    /// target: a stored live verdict is what makes the run's evidence frozen, so a bind after it would report keys
    /// the verdict never saw.
    /// </remarks>
    private async Task<Guid> SeedLiveEvaluationAsync(RunCreationTests.World world, Guid runId)
    {
        var revision = Guid.CreateVersion7();
        var policyHash = "sha256:" + new string('a', 64);

        await _fixture.ExecuteAsync(
            world.Database,
            $$"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select '{{revision:D}}', '{{world.Org:D}}', '{{world.Project:D}}', coalesce(max(revision_no), 0) + 1,
                   '{{policyHash}}', '{}', 'probe'
              from public.policy_revision where org_id = '{{world.Org:D}}' and project_id = '{{world.Project:D}}';
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            values ('{{Guid.CreateVersion7():D}}', '{{world.Org:D}}', '{{runId:D}}', 1, '{{revision:D}}', '{{policyHash}}',
                    'sha256:{{new string('c', 64)}}', 'complete', '{}', 'all_passed', 'PASS', '{}', '1.0.0',
                    'live', 'sha256:{{new string('b', 64)}}', now());
            """).ConfigureAwait(false);

        return revision;
    }

    [Fact]
    public async Task ExplicitBind_DraftRelease_422ReleaseNotActive_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var draft = await ReleaseWithAsync(world, "bind-draft", state: null, ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(world, draft, runId);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("release_not_active");
        (await BindingCountAsync(world, draft, runId)).ShouldBe(0);
    }

    /// <summary>The state rule precedes the live-evaluation rule: a draft release answers its own refusal.</summary>
    [Fact]
    public async Task ExplicitBind_DraftReleaseWithLiveEvaluation_422ReleaseNotActive()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var draft = await ReleaseWithAsync(world, "bind-draft-live", state: null, ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        await SeedLiveEvaluationAsync(world, runId);

        var answer = await BindAsync(world, draft, runId);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("release_not_active");
        (await BindingCountAsync(world, draft, runId)).ShouldBe(0);
    }

    [Fact]
    public async Task BindClosedAfterLiveEvaluation_409_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-closed", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        await SeedLiveEvaluationAsync(world, runId);

        var answer = await BindAsync(world, releaseId, runId);

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("release_bind_closed");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = 'orchestrator.release.bound'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task TenantIsolation_CrossOrgReleaseAndRun_404SameEnvelope()
    {
        await using var worldA = await RunCreationTests.StartAsync(_fixture, slug: "alpha");
        await using var worldB = await RunCreationTests.StartAsync(_fixture, slug: "beta");
        var releaseA = await ReleaseWithAsync(worldA, "bind-tenant-a", "active", ("web", 'a'));
        var releaseB = await ReleaseWithAsync(worldB, "bind-tenant-b", "active", ("web", 'a'));
        var runA = await RunWithAsync(worldA, Components(("web", 'a')));
        var runB = await RunWithAsync(worldB, Components(("web", 'a')));

        var foreignRelease = await BindAsync(worldB, releaseA, runB);
        var foreignRun = await BindAsync(worldB, releaseB, runA);
        var random = await BindAsync(worldB, releaseB, Guid.CreateVersion7());

        foreach (var refused in new[] { foreignRelease, foreignRun, random })
        {
            refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
            refused.ErrorCode.ShouldBe("not_found");
        }

        // A foreign release, a foreign run and a random id are the same answer: the refusals compare after the
        // per-request id is normalised, so no refusal is distinguishable and none echoes an id it could not see.
        CancelRunTestData.Envelope(foreignRelease).ShouldBe(CancelRunTestData.Envelope(random));
        CancelRunTestData.Envelope(foreignRun).ShouldBe(CancelRunTestData.Envelope(random));
        (await BindingCountAsync(worldA, releaseA, runA)).ShouldBe(0);
        (await BindingCountAsync(worldB, releaseB, runB)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            worldB.Database,
            $"select count(*) from public.release_run where org_id in ('{worldA.Org:D}', '{worldB.Org:D}')"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task ExplicitBind_CallerWithoutRunCreate_403_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-forbidden", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var answer = await BindAsync(
            world,
            releaseId,
            runId,
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    /// <summary>
    /// The cookie session's CSRF gate sits in front of the bind route: the session cookie alone is refused, and the
    /// same request with the XSRF header and the antiforgery cookie writes the binding.
    /// </summary>
    [Fact]
    public async Task Bind_CookieSession_WithoutXsrfPair_403CsrfFailed_AndWithThePair_201()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project, revision) = await SeedSessionTenantAsync(database);
        var parent = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, database, org, 1);
        var releaseId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var digest = Digest('a');

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.release (id, org_id, project_id, name, created_by)
            values ('{releaseId:D}', '{org:D}', '{project:D}', 'bind-cookie-session', 'probe');
            insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id)
            values ('{Guid.CreateVersion7():D}', '{org:D}', '{releaseId:D}', 'web', '{digest}', '{revision:D}');
            update public.release set state = 'active' where id = '{releaseId:D}';
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes, components)
            select '{runId:D}', s.org_id, s.project_id, s.manifest_id, s.suite_revision_id, s.environment_revision_id,
                   now() + interval '2 hours', 1440, jsonb_build_object('web', '{digest}')
              from public.run s where s.id = '{parent.RunId:D}';
            """);

        await using var host = ReleaseSessionHost.Start(_fixture.AppConnectionString(database));
        using var client = host.CreateSessionClient();
        var pair = await host.SignInAsync(client);

        var refused = await BindAsCookieAsync(client, pair.SessionOnly(), org, releaseId, runId, xsrf: null);
        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.Body.GetRawText());
        refused.ErrorCode.ShouldBe("csrf_failed");
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.release_run where org_id = '{org:D}'")).ShouldBe(0);

        var accepted = await BindAsCookieAsync(client, pair.Both(), org, releaseId, runId, xsrf: pair.Xsrf);
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.release_run where org_id = '{org:D}' and release_id = '{releaseId:D}' "
            + $"and run_id = '{runId:D}' and component_key = 'web'")).ShouldBe(1);
    }

    /// <summary>One bind as the browser: the cookie pair, the org header, an idempotency key and the XSRF header.</summary>
    private static async Task<ApiAnswer> BindAsCookieAsync(
        HttpClient client,
        string cookies,
        Guid org,
        Guid releaseId,
        Guid runId,
        string? xsrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/releases/{releaseId:D}/runs");
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("X-Evidgate-Org", org.ToString("D"));
        request.Headers.Add("Idempotency-Key", NewKey());
        if (xsrf is not null)
        {
            request.Headers.Add(XsrfHeader, xsrf);
        }

        request.Content = new StringContent(BindBody(runId), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }
}

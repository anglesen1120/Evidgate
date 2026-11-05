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
                   '{{policyHash}}', '{}', '{{Guid.CreateVersion7():D}}'
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

        // The same run against an active release of the same project: 201, so the draft refusal above is the state
        // rule rather than a pair that could never bind.
        var active = await ReleaseWithAsync(world, "bind-draft-control", "active", ("web", 'a'));
        var control = await BindAsync(world, active, runId);
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
        (await BindingCountAsync(world, active, runId)).ShouldBe(1);
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
        var unknownRelease = await BindAsync(worldB, Guid.CreateVersion7(), runB);
        var unknownRun = await BindAsync(worldB, releaseB, Guid.CreateVersion7());

        foreach (var refused in new[] { foreignRelease, foreignRun, unknownRelease, unknownRun })
        {
            refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
            refused.ErrorCode.ShouldBe("not_found");
        }

        // All four refusals are one target-neutral answer: another tenant's release or run cannot be told apart from
        // an id that never existed, and neither can the two kinds be told apart from each other.
        var envelope = CancelRunTestData.Envelope(unknownRelease);
        CancelRunTestData.Envelope(foreignRelease).ShouldBe(envelope);
        CancelRunTestData.Envelope(unknownRun).ShouldBe(envelope);
        CancelRunTestData.Envelope(foreignRun).ShouldBe(envelope);
        unknownRun.Body.GetProperty("error").GetProperty("details").GetArrayLength().ShouldBe(
            0, "the refusal names neither id it could not resolve");
        (await BindingCountAsync(worldA, releaseA, runA)).ShouldBe(0);
        (await BindingCountAsync(worldB, releaseB, runB)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            worldB.Database,
            $"select count(*) from public.release_run where org_id in ('{worldA.Org:D}', '{worldB.Org:D}')"))
            .ShouldBe(0, "no refusal wrote a binding for either tenant");

        // The same caller and the same route, on its own pair: 201 with the one bound key, so the refusals above are
        // about the tenant, not about a caller who could never bind anything.
        var own = await BindAsync(worldB, releaseB, runB);
        own.Status.ShouldBe(HttpStatusCode.Created, own.ErrorMessage);
        BoundOf(own).Keys.ShouldBe(["web"]);
        (await BindingCountAsync(worldB, releaseB, runB)).ShouldBe(1);
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

        // The same route under an identity that does hold `run:create` - the world's own member - so the 403 above is
        // the permission stage. A service principal is outside that ceiling by design, so it is not the control.
        var granted = await BindAsync(world, releaseId, runId);

        granted.Status.ShouldBe(HttpStatusCode.Created, granted.ErrorMessage);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(1);
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
        var releaseId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var suite = Guid.CreateVersion7();
        var suiteRevision = Guid.CreateVersion7();
        var manifest = Guid.CreateVersion7();
        var digest = Digest('a');

        // The run is written here rather than through the plan seeder: the session tenant already owns the
        // environment and its revision, and the seeder would reuse the environment row while inserting a revision
        // that points at its own id. One suite, one manifest and one run keep the fixture to this tenant's rows.
        await _fixture.ExecuteAsync(
            database,
            $$"""
            insert into public.release (id, org_id, project_id, name, created_by)
            values ('{{releaseId:D}}', '{{org:D}}', '{{project:D}}', 'bind-cookie-session', 'probe');
            insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id)
            values ('{{Guid.CreateVersion7():D}}', '{{org:D}}', '{{releaseId:D}}', 'web', '{{digest}}', '{{revision:D}}');
            update public.release set state = 'active' where id = '{{releaseId:D}}';
            insert into public.suite (id, org_id, project_id, name, slug)
            values ('{{suite:D}}', '{{org:D}}', '{{project:D}}', 'bind-cookie-session', 'bind-cookie-{{suite:N}}');
            insert into public.suite_revision
              (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values ('{{suiteRevision:D}}', '{{org:D}}', '{{suite:D}}', 1, 'sha256:{{new string('d', 64)}}',
                    '{"kind":"none"}', '{"max_retries":0}');
            insert into public.manifest
              (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id,
               shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values ('{{manifest:D}}', '{{org:D}}', '{{project:D}}', 'sha256:{{new string('e', 64)}}', 1,
                    '{{suiteRevision:D}}', '{{revision:D}}', '{}', '{}', '{}', 0, '{}', 'probe');
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes, components)
            values ('{{runId:D}}', '{{org:D}}', '{{project:D}}', '{{manifest:D}}', '{{suiteRevision:D}}',
                    '{{revision:D}}', now() + interval '2 hours', 1440, jsonb_build_object('web', '{{digest}}'));
            """);

        await using var host = CookieSessionHost.Start(_fixture.AppConnectionString(database));
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

    /// <summary>
    /// A fork run's ref is advisory-only, so its build is no evidence of this release: the bind refuses it 422
    /// <c>fork_run_not_bindable</c> before any write. A non-fork run of the same project, same components, binds - so
    /// the refusal is the identity and not the pair.
    /// </summary>
    [Fact]
    public async Task ExplicitBind_ForkRun_422ForkRunNotBindable_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-fork", "active", ("web", 'a'));
        var forkRun = await RunWithForkIdentityAsync(world);

        // The fixture is only meaningful if the row really is a fork: the identity is immutable, so the API is the
        // one way to record it and this read is what proves it was recorded.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select is_fork from public.run where id = '{forkRun:D}'")).ShouldBeTrue();

        var refused = await BindAsync(world, releaseId, forkRun);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("fork_run_not_bindable");
        (await BindingCountAsync(world, releaseId, forkRun)).ShouldBe(0);

        var control = await BindAsync(world, releaseId, await RunWithAsync(world, Components(("web", 'a'))));
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
    }

    /// <summary>
    /// One run whose recorded identity says fork: the create body carries <c>ref.is_fork</c> and the caller has no
    /// verified CI claim, which is the only path that stores it (a verified claim forces <c>false</c>).
    /// </summary>
    /// <param name="world">The suite's tenant.</param>
    private static async Task<Guid> RunWithForkIdentityAsync(RunCreationTests.World world)
    {
        var body = "{\"ref\":{\"is_fork\":true},"
            + RunBodyWith(world.ManifestId, Components(("web", 'a'))).TrimStart('{');
        var answer = await RunCreationTests.CreateRunAsync(world, body, caller: UserCaller(world));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer.Id("run_id");
    }

    /// <summary>
    /// One run row recorded under another project, for the project predicate of the run lookup: the row carries this
    /// suite's revisions and only its <c>project_id</c> differs, so the lookup's project filter is what must miss it.
    /// </summary>
    /// <param name="world">The suite's tenant.</param>
    /// <param name="project">The project to record on the run.</param>
    /// <param name="components">The recorded component digests, as jsonb.</param>
    private async Task<Guid> SeedRunInAsync(
        RunCreationTests.World world,
        Guid project,
        string components)
    {
        var runId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, components,
               collection_deadline_at, late_window_minutes)
            select '{runId:D}', '{world.Org:D}', '{project:D}', m.id, m.suite_revision_id, m.environment_revision_id,
                   '{components}'::jsonb, now() + interval '1 hour', 30
              from public.manifest m
             where m.org_id = '{world.Org:D}' and m.id = '{world.ManifestId:D}';
            """).ConfigureAwait(false);

        return runId;
    }

    /// <summary>
    /// A credential narrowed to one project gets the same 404 for another project's release as for an id that never
    /// existed, on the read and on the bind: a 403 would confirm the id exists elsewhere in its org. Its own project
    /// is served and bound normally, so the refusals are the scope and not the credentials.
    /// </summary>
    [Fact]
    public async Task NarrowedCaller_OtherProjectRelease_404LikeAnUnknownId()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("narrowed"));
        var siblingRelease = await SeedReleaseAsync(world, sibling, "scope-sibling");
        var ownRelease = await ReleaseWithAsync(world, "scope-own", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        var narrowed = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: world.Project);

        var refusedRead = await GetReleaseAsync(world, siblingRelease, narrowed);
        var unknownRead = await GetReleaseAsync(world, Guid.CreateVersion7(), narrowed);
        refusedRead.Status.ShouldBe(HttpStatusCode.NotFound, refusedRead.ErrorMessage);
        refusedRead.ErrorCode.ShouldBe("not_found");
        CancelRunTestData.Envelope(refusedRead).ShouldBe(CancelRunTestData.Envelope(unknownRead));

        var refusedBind = await BindAsync(world, siblingRelease, runId, caller: narrowed);
        refusedBind.Status.ShouldBe(HttpStatusCode.NotFound, refusedBind.ErrorMessage);
        refusedBind.ErrorCode.ShouldBe("not_found");
        (await BindingCountAsync(world, siblingRelease, runId)).ShouldBe(0);

        // The same narrowed credential inside its own project: the read is served and the bind is written.
        (await GetReleaseAsync(world, ownRelease, narrowed)).Status.ShouldBe(HttpStatusCode.OK);
        (await BindAsync(world, ownRelease, runId, caller: narrowed)).Status.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// A sibling project of the same org is not a tenant boundary but it is a release boundary: another project's
    /// release answers exactly the 404 an id that never existed answers, on the bind and on the read, and the same
    /// caller's own pair still binds. Nothing here becomes an oracle over a neighbouring project's declarations.
    /// </summary>
    [Fact]
    public async Task SiblingProject_ReleaseAndRun_Are404LikeAnUnknownId()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var siblingRelease = await SeedReleaseAsync(world, sibling, "bind-sibling");
        var ownRelease = await ReleaseWithAsync(world, "bind-sibling-own", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        var foreignRelease = await BindAsync(world, siblingRelease, runId);
        var unknownRelease = await BindAsync(world, Guid.CreateVersion7(), runId);
        var unknownRun = await BindAsync(world, ownRelease, Guid.CreateVersion7());

        foreach (var refused in new[] { foreignRelease, unknownRelease, unknownRun })
        {
            refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
            refused.ErrorCode.ShouldBe("not_found");
        }

        // Both sides of the pair answer one target-neutral refusal, so a sibling project's release is indistinguishable
        // from an id that never existed, and from a run that never existed under this project's own release.
        var envelope = CancelRunTestData.Envelope(unknownRelease);
        CancelRunTestData.Envelope(foreignRelease).ShouldBe(envelope);
        CancelRunTestData.Envelope(unknownRun).ShouldBe(envelope);
        foreignRelease.Body.GetProperty("error").GetProperty("details").GetArrayLength().ShouldBe(
            0, "the refusal names neither id it could not resolve");

        // The run half of the pair is project-filtered too: a run recorded under the sibling project, paired with this
        // project's own release, answers the same 404 and adds nothing.
        var siblingRun = await SeedRunInAsync(world, sibling, Components(("web", 'a')));
        var foreignRunUnderOwnRelease = await BindAsync(world, ownRelease, siblingRun);
        foreignRunUnderOwnRelease.Status.ShouldBe(HttpStatusCode.NotFound, foreignRunUnderOwnRelease.ErrorMessage);
        CancelRunTestData.Envelope(foreignRunUnderOwnRelease).ShouldBe(CancelRunTestData.Envelope(unknownRun));

        // The read stays org-scoped by design - a project is not a read boundary, so an unnarrowed caller sees a
        // sibling project's release exactly as it sees its own - while the bind is closed because it resolves a run
        // under the release's own project. The refused side is the write.
        var foreignRead = await GetReleaseAsync(world, siblingRelease);
        foreignRead.Status.ShouldBe(HttpStatusCode.OK, foreignRead.ErrorMessage);
        (await GetReleaseAsync(world, ownRelease)).Status.ShouldBe(HttpStatusCode.OK);

        // The positive control: the same caller and the same run, against its own project's release.
        var own = await BindAsync(world, ownRelease, runId);
        own.Status.ShouldBe(HttpStatusCode.Created, own.ErrorMessage);
        var ownRead = await GetReleaseAsync(world, ownRelease);
        ownRead.Status.ShouldBe(HttpStatusCode.OK, ownRead.ErrorMessage);
        (await BindingCountAsync(world, ownRelease, runId)).ShouldBe(1);
    }

    /// <summary>One active release of <paramref name="project"/> via the owner, with the world's environment revision.</summary>
    /// <param name="world">The suite's tenant.</param>
    /// <param name="project">The project that owns it, which may be a sibling of the run's.</param>
    /// <param name="name">Its name.</param>
    private async Task<Guid> SeedReleaseAsync(RunCreationTests.World world, Guid project, string name)
    {
        var releaseId = Guid.CreateVersion7();
        var environment = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();

        // A release component's revision must belong to the release's own project (the release_component_project
        // trigger), so the sibling project gets its own environment and revision.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.environment (id, org_id, project_id, environment_key, name)
            values ('{environment:D}', '{world.Org:D}', '{project:D}', 'staging', 'Staging')
            on conflict (org_id, project_id, environment_key) do nothing;
            insert into public.environment_revision
              (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            select '{revision:D}', '{world.Org:D}', '{project:D}', e.id, 1, jsonb_build_object(),
                   jsonb_build_object(), 'sha256:{new string('c', 64)}', 'probe'
              from public.environment e
             where e.org_id = '{world.Org:D}' and e.project_id = '{project:D}' and e.environment_key = 'staging'
            on conflict do nothing;
            insert into public.release (id, org_id, project_id, name, created_by)
            values ('{releaseId:D}', '{world.Org:D}', '{project:D}', '{name}', 'probe');
            insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id)
            values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{releaseId:D}', 'web', '{Digest('a')}',
                    (select id from public.environment_revision
                      where org_id = '{world.Org:D}' and project_id = '{project:D}'));
            update public.release set state = 'active' where id = '{releaseId:D}';
            """).ConfigureAwait(false);

        return releaseId;
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
            request.Headers.Add(CookieSessionHost.XsrfHeader, xsrf);
        }

        request.Content = new StringContent(BindBody(runId), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }
}

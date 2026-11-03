using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T4.1/T4.2 concurrency through the real host and a real PostgreSQL: the project row lock the
/// activation takes <c>FOR NO KEY UPDATE</c> serialises it against the quarantine create (which takes the same
/// row <c>FOR SHARE</c>), so a case in the activated revision's critical list can never be both quarantined and
/// activated, two simultaneous activations leave exactly one active row, a project row held past
/// <c>lock_timeout</c> is refused 503 busy with no write, and no pair deadlocks. The run-create reader takes no
/// lock (finding F4), so it is never serialised against the activation: it sees a revision that was active at
/// some commit.
/// </summary>
/// <remarks>
/// Every racing pair runs under <see cref="Task.WhenAll(System.Threading.Tasks.Task[])"/> with a 60-second
/// <c>WaitAsync</c>: a deadlock surfaces as a timeout rather than a hung test. Each task is a real request or a
/// real store/contracts call on its own connection, so the locks are the production ones.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class PolicyActivationConcurrencyTests
{
    private const string BodyA = """{"flaky_budget":3,"required_suites":["smoke"]}""";

    private const string BodyB = """{"flaky_budget":4,"required_suites":["smoke"]}""";

    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(60);

    private readonly PostgresTenancyFixture _fixture;

    public PolicyActivationConcurrencyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Case,
        Guid Maintainer,
        Guid MaintainerMembership) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public Action<HttpRequestHeaders> AsMaintainer => TenancyApiFactory.Resolved(user: Maintainer, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var (caseId, maintainer, membership) = await SeedCaseAndMaintainerAsync(database, client, user, org, project);

        return new World(api, client, database, user, org, project, caseId, maintainer, membership);
    }

    /// <summary>One catalog case and an active <c>test_maintainer</c> membership (the only role holding <c>quarantine:write</c>).</summary>
    private async Task<(Guid Case, Guid User, Guid Membership)> SeedCaseAndMaintainerAsync(
        string database, HttpClient client, Guid admin, Guid org, Guid project)
    {
        var caseId = Guid.CreateVersion7();
        var maintainer = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // 0044's `membership_user_fk` makes the membership's user_id name a real account.
            await scope.ExecuteAsync(
                """insert into public."user" (id, email) values (@id, @email)""",
                ("id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            await scope.ExecuteAsync(
                "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
                ("id", membership), ("org_id", org), ("user_id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            await scope.ExecuteAsync(
                """
                insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
                values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
                """,
                ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()), ("name", "checkout case"));
            await scope.CommitAsync();
        }

        var role = await _fixture.ScalarAsync<Guid>(database, "select id from public.role where code = 'test_maintainer' and org_id is null");
        var granted = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/memberships/{membership}/roles", TenancyApiFactory.Resolved(user: admin, orgHeader: org), new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return (caseId, maintainer, membership);
    }

    /// <summary>A revision whose critical list is exactly <paramref name="caseId"/>.</summary>
    private static async Task<Guid> CreateCriticalRevisionAsync(World world, Guid caseId) =>
        await CreateRevisionAsync(world, $$"""{"flaky_budget":3,"critical_cases":["{{caseId}}"]}""");

    private static async Task<Guid> CreateRevisionAsync(World world, string body)
    {
        var created = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/policies",
            world.Admin,
            body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id();
    }

    private static Task<ApiAnswer> ActivateAsync(World world, Guid revisionId) =>
        SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/policy-activation",
            world.Admin,
            $$"""{"policy_revision_id":"{{revisionId}}"}""",
            NewKey());

    private static Task<ApiAnswer> QuarantineAsync(World world, Guid caseId) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            "/v1/quarantine",
            world.AsMaintainer,
            new
            {
                project_id = world.Project,
                case_id = caseId,
                owner = world.MaintainerMembership,
                reason = "flaky case",
                expires_at = DateTimeOffset.UtcNow.AddDays(7),
            },
            NewKey());

    /// <summary>Sends a raw body verbatim; an <c>Idempotency-Key</c> is added only when one is supplied (the activation route requires one, the revision route takes none).</summary>
    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private async Task<long> ActiveCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_revision where state = 'active'");

    private async Task<long> ActivationAuditRowsAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.audit_log where action = 'policy.activate'");

    private async Task<long> ActiveQuarantineRowsAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry where state = 'active'");

    /// <summary>
    /// Reads the active policy through <see cref="IPolicyContracts"/> in its own tenant transaction — the run-create
    /// read of finding F4: it takes no project row lock, so it never waits on a concurrent activation.
    /// </summary>
    private static async Task<ActivePolicy?> ReadAsync(World world)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<IPolicyContracts>();

        var policy = await contracts.GetActivePolicyAsync(world.Project, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return policy;
    }

    // ------------------------------------------------------------------ (a) activation vs quarantine create

    [Fact]
    public async Task Activation_RacingAQuarantineCreate_Serialises_AndExactlyOneWriteSucceeds()
    {
        await using var world = await StartAsync();
        var revision = await CreateCriticalRevisionAsync(world, world.Case);

        var activation = ActivateAsync(world, revision);
        var quarantine = QuarantineAsync(world, world.Case);
        await Task.WhenAll(activation, quarantine).WaitAsync(RaceTimeout, TestContext.Current.CancellationToken);

        var activated = await activation;
        var quarantined = await quarantine;
        var activationWon = activated.Status == HttpStatusCode.OK;
        var quarantineWon = quarantined.Status is HttpStatusCode.Created or HttpStatusCode.OK;

        // The two writes serialise on the project row: quarantine first -> the activation is refused 422;
        // activation first -> the quarantine is refused 422. Both succeeding would break the critical-case rule.
        activationWon.ShouldNotBe(quarantineWon, "exactly one of the two writes must win the serialisation");

        if (!activationWon)
        {
            activated.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            activated.ErrorCode.ShouldBe("critical_case_quarantined");
        }

        if (!quarantineWon)
        {
            quarantined.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            quarantined.ErrorCode.ShouldBe("critical_case_not_quarantinable");
        }

        (await ActiveCountAsync(world)).ShouldBe(activationWon ? 1 : 0);
        (await ActiveQuarantineRowsAsync(world)).ShouldBe(quarantineWon ? 1 : 0);
    }

    // ------------------------------------------------------------------ (b) two simultaneous activations

    [Fact]
    public async Task TwoSimultaneousActivations_LeaveOneActiveRow_AndWriteTwoAuditRows()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        var second = await CreateRevisionAsync(world, BodyB);

        var answers = await Task.WhenAll(ActivateAsync(world, first), ActivateAsync(world, second))
            .WaitAsync(RaceTimeout, TestContext.Current.CancellationToken);

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        (await ActiveCountAsync(world)).ShouldBe(1);
        (await ActivationAuditRowsAsync(world)).ShouldBe(2);
    }

    // ------------------------------------------------------------------ (c) activation vs the run-create reader

    /// <summary>
    /// The run-create reader and an activation do not deadlock or time out. The reader takes no project row lock
    /// (finding F4), so the activation's <c>FOR NO KEY UPDATE</c> never waits on it; the reader's snapshot is a
    /// committed active revision (one that was active at some commit, never a torn pointer), and exactly one
    /// revision is active afterwards.
    /// </summary>
    [Fact]
    public async Task Activation_RacingAReaderViaPolicyContracts_DoesNotDeadlock_AndTheReaderSeesACommittedPolicy()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, BodyA);
        (await ActivateAsync(world, first)).Status.ShouldBe(HttpStatusCode.OK);
        var second = await CreateRevisionAsync(world, BodyB);

        var reader = ReadAsync(world);
        var activation = ActivateAsync(world, second);
        await Task.WhenAll(reader, activation).WaitAsync(RaceTimeout, TestContext.Current.CancellationToken);

        (await activation).Status.ShouldBe(HttpStatusCode.OK);
        var seen = await reader;
        seen.ShouldNotBeNull();
        new[] { first, second }.ShouldContain(seen!.PolicyRevisionId);
        (await ActiveCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ (d) the project row held past lock_timeout

    /// <summary>
    /// 07.1.1 §8 lock-timeout row: an activation that cannot take the project row <c>FOR NO KEY UPDATE</c> within
    /// <c>lock_timeout</c> (5 s) answers 503 <c>unavailable</c> and writes nothing — the revision stays draft, no
    /// audit row and no pointer. The row is held here on an owner connection, which bypasses the store.
    /// </summary>
    [Fact]
    public async Task Activation_ProjectRowHeldPastLockTimeout_Is503Busy_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, BodyA);

        // The holder keeps the project row locked FOR NO KEY UPDATE until this scope is disposed (rolled back).
        await using var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await holder.ScalarAsync<int>(
            "select 1 from public.project where id = @project_id for no key update",
            ("project_id", world.Project))).ShouldBe(1);

        var answer = await ActivateAsync(world, revision)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        answer.ErrorCode.ShouldBe("unavailable");

        // The refusal happens before any write.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.policy_revision where id = '{revision}'")).ShouldBe("draft");
        (await ActivationAuditRowsAsync(world)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_activation")).ShouldBe(0);
    }
}

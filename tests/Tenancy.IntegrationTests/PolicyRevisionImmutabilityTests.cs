using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Domain;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T1.1/T1.2 through a real PostgreSQL: the migration's table with FORCE RLS, immutability at the database
/// (app-role grants and the trigger), the append-only store's dedupe and numbering, and the tenant boundary.
/// </summary>
[Trait("Category", "Docker")]
public sealed class PolicyRevisionImmutabilityTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public PolicyRevisionImmutabilityTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A well-formed content address of the requested fill character.</summary>
    private static string Hash(char fill) => "sha256:" + new string(fill, 64);

    /// <summary>A JSON object body carrying <paramref name="marker"/>.</summary>
    private static string Body(string marker) => $"{{\"marker\":\"{marker}\"}}";

    /// <summary>One append through the real store inside its own tenant scope and transaction.</summary>
    private static async Task<PolicyRevisionAppend> AppendAsync(World world, string body)
    {
        var outcome = await AppendOutcomeAsync(world, body);
        outcome.IsSuccess.ShouldBeTrue();
        outcome.Value.Hash.ShouldBe(CanonicalHash.Compute(body));

        return outcome.Value;
    }

    /// <summary>The raw outcome of one append, for the cases that expect a typed failure.</summary>
    private static async Task<Result<PolicyRevisionAppend>> AppendOutcomeAsync(World world, string body)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var store = services.ServiceProvider.GetRequiredService<PolicyRevisionStore>();

        var appended = await store.AppendAsync(world.Project, body, world.User, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return appended;
    }

    /// <summary>Runs one statement as the owner role in its own transaction and returns the server error.</summary>
    private async Task<PostgresException> OwnerErrorAsync(World world, string sql)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        return await owner.CaptureErrorAsync(sql);
    }

    // ------------------------------------------------------------------ (a) migration

    [Fact]
    public async Task Migration_AppliesTheTable_WithForcedRls()
    {
        await using var world = await StartAsync();

        (await _fixture.TableExistsAsync(world.Database, "policy_revision")).ShouldBeTrue();
        var rls = await _fixture.RowSecurityAsync(world.Database, "policy_revision");
        rls.Enabled.ShouldBeTrue();
        rls.Forced.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ (b) app role

    [Fact]
    public async Task AppRole_CannotUpdateOrDelete_AtTheDatabase()
    {
        await using var world = await StartAsync();
        await AppendAsync(world, Body("a"));

        foreach (var statement in new[]
        {
            "update public.policy_revision set body = '{\"x\":1}'::jsonb",
            "delete from public.policy_revision",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

            (await scope.CaptureErrorAsync(statement)).SqlState.ShouldBe("42501", statement);
        }
    }

    // ------------------------------------------------------------------ (b2) the 07.1.1-T4 column grant and index

    [Fact]
    public async Task AppRole_ColumnGrant_AllowsTheTwoLifecycleMoves_AndRefusesEverythingElse()
    {
        await using var world = await StartAsync();
        var appended = await AppendAsync(world, Body("a"));
        var where = $"where id = '{appended.Id}'";

        // body, hash and the identity columns stay outside the app role's `UPDATE (state)` grant (0033): 42501
        // before the immutability trigger is reached.
        foreach (var set in new[]
        {
            "body = '{\"x\":1}'::jsonb",
            $"hash = '{Hash('b')}'",
            $"org_id = '{Guid.CreateVersion7()}'",
            $"project_id = '{Guid.CreateVersion7()}'",
            "revision_no = 9",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.CaptureErrorAsync($"update public.policy_revision set {set} {where}")).SqlState.ShouldBe("42501", set);
        }

        // draft -> active is granted, and the database owns the version bump (the 07.1.1-T4 activation path).
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.policy_revision set state = 'active' {where}")).ShouldBe(1);
            (await scope.ScalarAsync<int>($"select version from public.policy_revision {where}")).ShouldBe(2);
            await scope.CommitAsync();
        }

        // active -> draft is outside the state machine: 23514, whoever issues it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"update public.policy_revision set state = 'draft' {where}")).SqlState.ShouldBe("23514");
        }

        // active -> retired is the other granted move.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.policy_revision set state = 'retired' {where}")).ShouldBe(1);
            await scope.CommitAsync();
        }

        // retired -> active is terminal: the trigger refuses it even though the column grant allows the statement.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"update public.policy_revision set state = 'active' {where}")).SqlState.ShouldBe("23514");
        }
    }

    [Fact]
    public async Task OneActiveRevisionPerProject_IsEnforcedByThePartialUniqueIndex_AndIsPerProject()
    {
        await using var world = await StartAsync();
        var first = await AppendAsync(world, Body("a"));
        var second = await AppendAsync(world, Body("b"));
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "second");
        var inOther = await AppendAsync(world with { Project = otherProject }, Body("a"));

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.policy_revision set state = 'active' where id = '{first.Id}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        // A second active row in the same project is refused by policy_revision_active_key (23505).
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"update public.policy_revision set state = 'active' where id = '{second.Id}'"))
                .SqlState.ShouldBe("23505");
        }

        // The index is per project, so the same move is allowed for another project of the org.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.policy_revision set state = 'active' where id = '{inOther.Id}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_revision where state = 'active' and org_id = '{world.Org}'")).ShouldBe(2);
    }

    // ------------------------------------------------------------------ (c) owner role: the trigger

    [Fact]
    public async Task OwnerRole_Trigger_RefusesDeleteAndBodyChange_AndRetiredToActive_ButAllowsDraftToActive()
    {
        await using var world = await StartAsync();
        var appended = await AppendAsync(world, Body("a"));

        (await OwnerErrorAsync(world, "delete from public.policy_revision"))
            .MessageText.ShouldContain("revision_immutable");
        (await OwnerErrorAsync(world, $"update public.policy_revision set body = '{{\"x\":1}}'::jsonb where id = '{appended.Id}'"))
            .SqlState.ShouldBe("23514");
        (await OwnerErrorAsync(world, $"update public.policy_revision set hash = '{Hash('b')}' where id = '{appended.Id}'"))
            .SqlState.ShouldBe("23514");

        // draft -> active is the 07.1.1-T4 path and must stay open; the trigger bumps version (00 §7).
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync($"update public.policy_revision set state = 'active' where id = '{appended.Id}'")).ShouldBe(1);
            (await owner.ScalarAsync<string>($"select state from public.policy_revision where id = '{appended.Id}'")).ShouldBe("active");
            (await owner.ScalarAsync<int>($"select version from public.policy_revision where id = '{appended.Id}'")).ShouldBe(2);
            await owner.CommitAsync();
        }

        // active -> retired is the other legal move; version moves to 3.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync($"update public.policy_revision set state = 'retired' where id = '{appended.Id}'")).ShouldBe(1);
            (await owner.ScalarAsync<int>($"select version from public.policy_revision where id = '{appended.Id}'")).ShouldBe(3);
            await owner.CommitAsync();
        }

        (await OwnerErrorAsync(world, $"update public.policy_revision set state = 'active' where id = '{appended.Id}'"))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task Trigger_BumpsVersionOnATransition_IgnoringACallerSuppliedVersion()
    {
        await using var world = await StartAsync();
        var appended = await AppendAsync(world, Body("a"));

        // A transition overwrites whatever version the caller sent: the database owns the increment (00 §7).
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync($"update public.policy_revision set state = 'active', version = 99 where id = '{appended.Id}'")).ShouldBe(1);
        (await owner.ScalarAsync<int>($"select version from public.policy_revision where id = '{appended.Id}'")).ShouldBe(2);
        await owner.CommitAsync();
    }

    // ------------------------------------------------------------------ (d) store contract

    [Fact]
    public async Task Store_AppendsOnce_ThenReturnsTheSameRow_AndANewHashGetsTheNextNumber()
    {
        await using var world = await StartAsync();

        var first = await AppendAsync(world, Body("a"));
        var again = await AppendAsync(world, Body("a"));
        var other = await AppendAsync(world, Body("b"));

        first.Created.ShouldBeTrue();
        first.RevisionNo.ShouldBe(1);
        again.Created.ShouldBeFalse();
        again.Id.ShouldBe(first.Id);
        again.RevisionNo.ShouldBe(1);
        other.Created.ShouldBeTrue();
        other.RevisionNo.ShouldBe(2);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_revision where org_id = '{world.Org}'")).ShouldBe(2);
    }

    [Fact]
    public async Task Store_UnknownOrSoftDeletedProject_IsNotFound_AndWritesNoRow()
    {
        await using var world = await StartAsync();

        var unknown = await AppendOutcomeAsync(world with { Project = Guid.CreateVersion7() }, Body("a"));
        unknown.IsSuccess.ShouldBeFalse();
        unknown.Error!.Status.ShouldBe(404);

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync($"update public.project set status = 'deleted' where id = '{world.Project}'")).ShouldBe(1);
            await owner.CommitAsync();
        }

        var deleted = await AppendOutcomeAsync(world, Body("b"));
        deleted.IsSuccess.ShouldBeFalse();
        deleted.Error!.Status.ShouldBe(404);

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_revision where org_id = '{world.Org}'")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ (e) no tenant context

    [Fact]
    public async Task ASessionWithNoOrg_ReadsZeroRows_NotAnError()
    {
        await using var world = await StartAsync();
        await AppendAsync(world, Body("a"));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await scope.ScalarAsync<long>("select count(*) from public.policy_revision")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ (f) another org

    [Fact]
    public async Task AnotherOrg_CannotSeeTheRevision()
    {
        await using var world = await StartAsync();
        await AppendAsync(world, Body("a"));

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database),
            Guid.CreateVersion7());

        (await scope.ScalarAsync<long>("select count(*) from public.policy_revision")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ QA: negative and boundary

    private static string InsertSql(Guid org, Guid project, int no, string hash, string body = "{}", string state = "draft", int version = 1) =>
        "insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by, version) " +
        $"values ('{Guid.CreateVersion7()}', '{org}', '{project}', {no}, '{hash}', '{body}'::jsonb, '{state}', '{Guid.CreateVersion7()}', {version})";

    [Fact]
    public async Task AppRole_CannotInsertARowForAnotherOrg_WithCheck()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("beta"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(InsertSql(otherOrg, otherProject, 1, Hash('c')))).SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task CompositeFk_RefusesAProjectOfAnotherOrg()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("beta"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(InsertSql(world.Org, otherProject, 1, Hash('c')))).SqlState.ShouldBe("23503");
    }

    [Theory]
    [InlineData("sha256:" + "A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0")]
    [InlineData("sha256:abc")]
    [InlineData("md5:00000000000000000000000000000000")]
    [InlineData("")]
    public async Task HashCheck_RefusesMalformedHashes(string hash)
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(InsertSql(world.Org, world.Project, 1, hash))).SqlState.ShouldBe("23514");
    }

    [Theory]
    [InlineData("[]", "draft", 1, 1)]
    [InlineData("1", "draft", 1, 1)]
    [InlineData("{}", "bogus", 1, 1)]
    [InlineData("{}", "draft", 0, 1)]
    [InlineData("{}", "draft", 1, 0)]
    public async Task TableChecks_RefuseBadBodyStateRevisionNoAndVersion(string body, string state, int no, int version)
    {
        await using var world = await StartAsync();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.CaptureErrorAsync(InsertSql(world.Org, world.Project, no, Hash('d'), body, state, version)))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task UniqueKeys_RefuseDuplicateHashAndDuplicateRevisionNo()
    {
        await using var world = await StartAsync();
        var appended = await AppendAsync(world, Body("a"));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.CaptureErrorAsync(InsertSql(world.Org, world.Project, 2, appended.Hash))).SqlState.ShouldBe("23505");

        await using var scope2 = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope2.CaptureErrorAsync(InsertSql(world.Org, world.Project, 1, Hash('b')))).SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task Trigger_RefusesIdentityChanges_VersionOnlyBump_AndDraftToRetired()
    {
        await using var world = await StartAsync();
        var appended = await AppendAsync(world, Body("a"));
        var where = $"where id = '{appended.Id}'";

        foreach (var set in new[]
        {
            $"org_id = '{Guid.CreateVersion7()}'",
            $"project_id = '{Guid.CreateVersion7()}'",
            "revision_no = 9",
            $"created_by = '{Guid.CreateVersion7()}'",
            "created_at = now() + interval '1 day'",
            "version = 2",
            "state = 'retired'",
        })
        {
            var error = await OwnerErrorAsync(world, $"update public.policy_revision set {set} {where}");
            error.SqlState.ShouldBe("23514", set);
        }

        // A same-state, same-value UPDATE is a permitted no-op.
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync($"update public.policy_revision set state = 'draft' {where}")).ShouldBe(1);
    }

    [Fact]
    public async Task Store_RejectsANonCanonicalisableOrNonObjectBody_BeforeAnyStatement()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<ArgumentException>(() => AppendAsync(world, "[1]"));
        await Should.ThrowAsync<ArgumentException>(() => AppendAsync(world, "not json"));
        await Should.ThrowAsync<ArgumentException>(() => AppendAsync(world, "{\"a\":1,\"a\":2}"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_revision where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Store_NumbersPerProject_AndConcurrentSameHashAppendsCollapseToOneRow()
    {
        await using var world = await StartAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AppendAsync(world, Body("e"))));

        results.Count(r => r.Created).ShouldBe(1);
        results.Select(r => r.Id).Distinct().Count().ShouldBe(1);
        (await AppendAsync(world, Body("f"))).RevisionNo.ShouldBe(2);

        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "second");
        var inOther = await AppendAsync(world with { Project = otherProject }, Body("e"));
        inOther.Created.ShouldBeTrue();
        inOther.RevisionNo.ShouldBe(1);
    }

    [Fact]
    public async Task Store_ConcurrentDistinctHashAppends_NumberExactlyOnceEach()
    {
        await using var world = await StartAsync();

        // All six transactions start before any is awaited, so their project-row locks overlap.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => AppendAsync(world, Body($"m{i}"))));

        results.ShouldAllBe(r => r.Created);
        results.Select(r => r.RevisionNo).OrderBy(no => no).ShouldBe(Enumerable.Range(1, 6));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.policy_revision where org_id = '{world.Org}'")).ShouldBe(6);
    }

    // ------------------------------------------------------------------ the EF write model

    [Fact]
    public async Task WriteModel_BuildsThroughTheProductionPath_AndMapsTheContractColumns()
    {
        await using var world = await StartAsync();

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, true, TestContext.Current.CancellationToken);
        await using var context = services.ServiceProvider.GetRequiredService<ITenantDbContextFactory<PolicyDbContext>>().Create();

        var entity = context.Model.FindEntityType(typeof(PolicyRevision)).ShouldNotBeNull();
        entity.GetTableName().ShouldBe("policy_revision");
        entity.GetSchema().ShouldBe("public");
        entity.GetProperties().Select(property => property.GetColumnName()).ShouldBe(
            ["id", "org_id", "project_id", "revision_no", "hash", "body", "state", "created_by", "created_at", "version"],
            ignoreOrder: true);
        entity.FindProperty(nameof(PolicyRevision.Body)).ShouldNotBeNull().GetColumnType().ShouldBe("jsonb");
    }
}

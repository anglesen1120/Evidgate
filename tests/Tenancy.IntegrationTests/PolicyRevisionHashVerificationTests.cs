using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Modules.Policy.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T2.2 through a real PostgreSQL: <c>GetPolicyRevisionQuery</c> recomputes the content address of the
/// stored <c>body</c> and refuses a row that no longer matches its <c>hash</c> column (the corruption detector of
/// finding T1-SEC-S2), while a row written through the normal path — and a body jsonb re-serialised with its own
/// key order — still verifies. The mutation cases write as the owner role with the immutability trigger disabled,
/// which is the only way a stored revision can change at all (the app role holds no <c>UPDATE</c> grant).
/// </summary>
[Trait("Category", "Docker")]
public sealed class PolicyRevisionHashVerificationTests
{
    private readonly PostgresTenancyFixture _fixture;

    public PolicyRevisionHashVerificationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, DedicatedDatabase Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDedicatedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// Inserts one revision as the owner role with the content address of the body's canonical form, so the row is
    /// exactly what the append-only path would have written. Raw owner SQL on purpose: the read path is under test,
    /// not the store's signature.
    /// </summary>
    private async Task<Guid> InsertAsync(World world, string body)
    {
        var id = Guid.CreateVersion7();
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        await owner.ExecuteAsync(
            "insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by) " +
            "values (@id, @org, @project, 1, @hash, @body::jsonb, 'draft', @by)",
            ("id", id),
            ("org", world.Org),
            ("project", world.Project),
            ("hash", CanonicalHash.Compute(body)),
            ("body", body),
            ("by", world.User));
        await owner.CommitAsync();

        return id;
    }

    /// <summary>Reads one revision through the real query handler inside a tenant query scope (00 §5.5 C1/C5).</summary>
    private static async Task<Result<PolicyRevisionView>> ReadAsync(World world, Guid id, Guid? org = null)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org ?? world.Org, null, true, TestContext.Current.CancellationToken);
        var handler = services.ServiceProvider
            .GetRequiredService<IQueryHandler<GetPolicyRevisionQuery, Result<PolicyRevisionView>>>();

        return await handler.Handle(new GetPolicyRevisionQuery(id), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Mutates one stored row as the owner with the <c>policy_revision_immutable</c> trigger disabled — the only
    /// way a committed revision can change — then re-enables it, so the corruption is the row's, not the trigger's.
    /// </summary>
    private async Task MutateAsync(World world, Guid id, string setClause, string value)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        await owner.ExecuteAsync("alter table public.policy_revision disable trigger policy_revision_immutable");
        await owner.ExecuteAsync(
            $"update public.policy_revision set {setClause} where id = @id",
            ("id", id),
            ("value", value));
        await owner.ExecuteAsync("alter table public.policy_revision enable trigger policy_revision_immutable");
        await owner.CommitAsync();
    }

    // ------------------------------------------------------------------ (a) the normal path

    [Fact]
    public async Task StoredRevision_ReadsBack_WithEveryField_AndMatchingHash()
    {
        await using var world = await StartAsync();
        var body = "{\"marker\":\"a\"}";
        var id = await InsertAsync(world, body);

        var result = await ReadAsync(world, id);

        result.IsSuccess.ShouldBeTrue();
        var view = result.Value;
        view.Id.ShouldBe(id);
        view.ProjectId.ShouldBe(world.Project);
        view.RevisionNo.ShouldBe(1);
        view.Hash.ShouldBe(CanonicalHash.Compute(body));
        CanonicalHash.Compute(view.Body).ShouldBe(CanonicalHash.Compute(body));
        view.State.ShouldBe(PolicyRevisionState.Draft);
        view.CreatedBy.ShouldBe(world.User);
        view.Version.ShouldBe(1);
    }

    // ------------------------------------------------------------------ (b) corruption: body

    [Fact]
    public async Task MutatedBody_FailsTheRead_WithTheCorruptionDetector()
    {
        await using var world = await StartAsync();
        var id = await InsertAsync(world, "{\"marker\":\"a\"}");
        await MutateAsync(world, id, "body = @value::jsonb", "{\"marker\":\"b\"}");

        var exception = await Should.ThrowAsync<PolicyRevisionCorruptedException>(() => ReadAsync(world, id));

        exception.RevisionId.ShouldBe(id);
        exception.StoredHash.ShouldBe(CanonicalHash.Compute("{\"marker\":\"a\"}"));
        exception.RecomputedHash.ShouldBe(CanonicalHash.Compute("{\"marker\":\"b\"}"));
    }

    // ------------------------------------------------------------------ (c) corruption: hash

    [Fact]
    public async Task MutatedHash_FailsTheRead_WithTheCorruptionDetector()
    {
        await using var world = await StartAsync();
        var body = "{\"marker\":\"a\"}";
        var id = await InsertAsync(world, body);
        var forged = "sha256:" + new string('0', 64);
        await MutateAsync(world, id, "hash = @value", forged);

        var exception = await Should.ThrowAsync<PolicyRevisionCorruptedException>(() => ReadAsync(world, id));

        exception.RevisionId.ShouldBe(id);
        exception.StoredHash.ShouldBe(forged);
        exception.RecomputedHash.ShouldBe(CanonicalHash.Compute(body));
    }

    // ------------------------------------------------------------------ (d) not found

    [Fact]
    public async Task MissingRevision_IsNotFound()
    {
        await using var world = await StartAsync();

        var result = await ReadAsync(world, Guid.CreateVersion7());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(404);
    }

    [Fact]
    public async Task AnotherOrgsRevision_IsNotFound_NeverForbidden()
    {
        await using var world = await StartAsync();
        var id = await InsertAsync(world, "{\"marker\":\"a\"}");

        var result = await ReadAsync(world, id, org: Guid.CreateVersion7());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(404);
    }

    // ------------------------------------------------------------------ (e) jsonb re-serialisation

    [Fact]
    public async Task KeyReorderedBody_StoredByJsonb_StillVerifies()
    {
        await using var world = await StartAsync();

        // jsonb re-serialises with its own key order and whitespace, so the text read back is not the text written;
        // the canonical form is order-independent, so the recomputed address still equals the stored one.
        var body = "{\"z\":1,\"a\":2}";
        var id = await InsertAsync(world, body);

        var result = await ReadAsync(world, id);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Body.ShouldNotBe(body);
        result.Value.Hash.ShouldBe(CanonicalHash.Compute(body));
    }

    [Theory]
    [InlineData("{\"a\":1.0}", "{\"a\":1}")]
    [InlineData("{\"a\":1e2}", "{\"a\":100}")]
    [InlineData("{\"a\":-0}", "{\"a\":0}")]
    [InlineData("{\"a\":1e-7}", "{\"a\":0.0000001}")]
    [InlineData("{\"a\":\"\\u00e9\"}", "{\"a\":\"é\"}")]
    [InlineData("{\"a\":\"\\ud834\\udd1e\"}", "{\"a\":\"𝄞\"}")]
    public async Task EquivalentNumbersAndUnicode_StoredByJsonb_StillVerify(string body, string equivalent)
    {
        await using var world = await StartAsync();
        var id = await InsertAsync(world, body);

        var result = await ReadAsync(world, id);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Hash.ShouldBe(CanonicalHash.Compute(equivalent));
    }

    [Fact]
    public async Task NonCanonicalisableStoredBody_IsCorruption_WithTheNotCanonicalisableReason()
    {
        await using var world = await StartAsync();
        var body = "{\"marker\":\"a\"}";
        var id = await InsertAsync(world, body);
        // jsonb accepts this numeric value, but it is outside RFC 8785's finite-double range.
        await MutateAsync(world, id, "body = @value::jsonb", "{\"a\":1e400}");

        var exception = await Should.ThrowAsync<PolicyRevisionCorruptedException>(() => ReadAsync(world, id));

        exception.RevisionId.ShouldBe(id);
        exception.StoredHash.ShouldBe(CanonicalHash.Compute(body));
        exception.RecomputedHash.ShouldBeNull();
        exception.Reason.ShouldBe(PolicyRevisionCorruptionReasons.NotCanonicalisable);
        exception.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task InexactNumberStoredByJsonb_FailsTheRead_WithTheInexactNumberReason()
    {
        await using var world = await StartAsync();
        // The hash is computed for the double-precision value 9007199254740992; the owner rewrites the stored
        // number to 9007199254740993, which jsonb keeps exactly but no double can represent (I-JSON §2.2).
        var body = "{\"marker\":9007199254740992}";
        var id = await InsertAsync(world, body);
        await MutateAsync(world, id, "body = @value::jsonb", "{\"marker\":9007199254740993}");

        var exception = await Should.ThrowAsync<PolicyRevisionCorruptedException>(() => ReadAsync(world, id));

        exception.RevisionId.ShouldBe(id);
        exception.StoredHash.ShouldBe(CanonicalHash.Compute(body));
        exception.RecomputedHash.ShouldBeNull();
        exception.Reason.ShouldBe(PolicyRevisionCorruptionReasons.InexactNumber);
        exception.ToString().ShouldNotContain("marker");
    }

    [Fact]
    public async Task Store_CanonicalEquivalentBody_ReturnsTheSameRow_AndReadsWithItsComputedHash()
    {
        await using var world = await StartAsync();
        var bodies = new[] { "{\"z\":1.0,\"a\":\"\\u00e9\"}", " { \"a\" : \"é\", \"z\" : 1 } " };
        PolicyRevisionAppend? first = null;
        foreach (var body in bodies)
        {
            await using var services = world.Api.Services.CreateAsyncScope();
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
            var store = services.ServiceProvider.GetRequiredService<PolicyRevisionStore>();

            var appended = await store.AppendAsync(world.Project, body, world.User, TestContext.Current.CancellationToken);

            appended.IsSuccess.ShouldBeTrue();
            appended.Value.Hash.ShouldBe(CanonicalHash.Compute(bodies[0]));
            appended.Value.RevisionNo.ShouldBe(1);
            if (first is null)
            {
                appended.Value.Created.ShouldBeTrue();
                first = appended.Value;
            }
            else
            {
                appended.Value.Created.ShouldBeFalse();
                appended.Value.Id.ShouldBe(first.Id);
                appended.Value.Hash.ShouldBe(first.Hash);
            }

            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_revision")).ShouldBe(1);
        var read = await ReadAsync(world, first!.Id);
        read.IsSuccess.ShouldBeTrue();
        read.Value.Hash.ShouldBe(first.Hash);
    }
}

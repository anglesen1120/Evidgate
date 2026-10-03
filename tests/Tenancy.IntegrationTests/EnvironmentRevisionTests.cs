using System.Net;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.1.2 AC1–AC7 through the real host and a real PostgreSQL: immutable, numbered, hashed and secret-free environment
/// revisions, their read path, the pointer and project foreign keys, and the permissions.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EnvironmentRevisionTests
{
    private readonly PostgresTenancyFixture _fixture;

    public EnvironmentRevisionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

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

        return new World(api, client, database, user, org, project);
    }

    private static object Config(string postgres = "18.0", string? url = null, string[]? refs = null, string? name = null, int? revisionNo = null) => new
    {
        name,
        revision_no = revisionNo,
        base_urls = new Dictionary<string, string> { ["web"] = url ?? "https://staging.example.test" },
        dependency_versions = new Dictionary<string, string> { ["postgres"] = postgres },
        secret_refs = refs ?? ["secret:db_password"],
    };

    private static Task<ApiAnswer> CreateAsync(World world, object body, string environment = "staging", Action<System.Net.Http.Headers.HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/environments/{environment}/revisions", caller ?? world.Admin, body, key ?? NewKey());

    [Fact]
    public async Task CreateEnvironmentRevision_HashStable_RevisionNoIncrements_AndIdenticalConfigReturnsExisting()
    {
        await using var world = await StartAsync();

        var first = await CreateAsync(world, Config(name: "Staging", refs: ["secret:b", "secret:a"]));
        var second = await CreateAsync(world, Config(postgres: "18.1"));
        var reverted = await CreateAsync(world, Config(refs: ["secret:a", "secret:b"]));

        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Body.GetProperty("revision_no").GetInt32().ShouldBe(1);
        first.Text("revision_hash")!.ShouldMatch("^sha256:[0-9a-f]{64}$");
        first.ETag.ShouldBe($"\"{first.Text("revision_hash")}\"");
        second.Status.ShouldBe(HttpStatusCode.Created);
        second.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        second.Text("revision_hash").ShouldNotBe(first.Text("revision_hash"));

        reverted.Status.ShouldBe(HttpStatusCode.OK);
        reverted.Body.GetProperty("created").GetBoolean().ShouldBeFalse();
        reverted.Id("revision_id").ShouldBe(first.Id("revision_id"));
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.environment_revision")).ShouldBe(2);
        (await _fixture.ScalarAsync<Guid>(world.Database, "select current_revision_id from public.environment")).ShouldBe(first.Id("revision_id"));
        (await _fixture.ScalarAsync<string>(world.Database, "select name from public.environment")).ShouldBe("Staging");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.audit_log where action = 'environment_revision.create'")).ShouldBe(3);
    }

    [Fact]
    public async Task CreateEnvironmentRevision_SecretValue_Is422_NoEcho_AndStoresNothing()
    {
        await using var world = await StartAsync();
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

        foreach (var body in new[]
        {
            Config(refs: ["secret:ok", "hunter2-plain"]),
            Config(url: $"https://ci:{token}@staging.example.test"),
            Config(url: $"https://staging.example.test/?access_token={token}"),
            Config(postgres: token),
        })
        {
            var answer = await CreateAsync(world, body);

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            answer.ErrorCode.ShouldBe("environment_revision_invalid");
            answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString().ShouldBe("secret_value_detected");
            answer.Body.GetRawText().ShouldNotContain(token);
            answer.Body.GetRawText().ShouldNotContain("hunter2");
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.environment_revision")).ShouldBe(0);
    }

    [Fact]
    public async Task CreateEnvironmentRevision_SchemaAndKeyAndProject_AreChecked()
    {
        await using var world = await StartAsync();

        var schema = await CreateAsync(world, new { base_urls = new { }, dependency_versions = new { postgres = 18 } });
        schema.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        schema.ErrorCode.ShouldBe("environment_revision_invalid");

        (await CreateAsync(world, Config(), environment: "Not_Valid")).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{Guid.CreateVersion7()}/environments/staging/revisions", world.Admin, Config(), NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.environment")).ShouldBe(0);
    }

    [Fact]
    public async Task CreateEnvironmentRevision_SameRevisionNo_Is409()
    {
        await using var world = await StartAsync();
        (await CreateAsync(world, Config(revisionNo: 1))).Status.ShouldBe(HttpStatusCode.Created);

        var stale = await CreateAsync(world, Config(postgres: "19.0", revisionNo: 1));

        stale.Status.ShouldBe(HttpStatusCode.Conflict);
        stale.ErrorCode.ShouldBe("revision_conflict");
        (await CreateAsync(world, Config(postgres: "19.0", revisionNo: 2))).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateEnvironmentRevision_SameKeySameResult_DifferentPayload409()
    {
        await using var world = await StartAsync();
        var key = NewKey();

        var first = await CreateAsync(world, Config(), key: key);
        var replay = await CreateAsync(world, Config(), key: key);
        var conflict = await CreateAsync(world, Config(postgres: "17.0"), key: key);

        replay.Status.ShouldBe(first.Status);
        replay.Id("revision_id").ShouldBe(first.Id("revision_id"));
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConcurrentCreates_GetDistinctConsecutiveNumbers()
    {
        await using var world = await StartAsync();
        (await CreateAsync(world, Config())).Status.ShouldBe(HttpStatusCode.Created);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => CreateAsync(world, Config(postgres: $"18.{i + 1}"))));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Created);
        answers.Select(answer => answer.Body.GetProperty("revision_no").GetInt32()).Order().ShouldBe(Enumerable.Range(2, 8));
    }

    [Fact]
    public async Task GetEnvironmentRevision_Etag_IsRevisionHash_With304_AndIsolation()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Config());
        var id = created.Id("revision_id");

        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/environment-revisions/{id}", world.Admin);
        read.Status.ShouldBe(HttpStatusCode.OK);
        read.ETag.ShouldBe($"\"{created.Text("revision_hash")}\"");
        read.Headers.CacheControl!.ToString().ShouldContain("immutable");
        read.Text("environment_key").ShouldBe("staging");
        read.Body.GetProperty("base_urls").GetProperty("web").GetString().ShouldBe("https://staging.example.test");
        read.Body.GetProperty("secret_refs")[0].GetString().ShouldBe("secret:db_password");

        using (var conditional = new HttpRequestMessage(HttpMethod.Get, $"/v1/environment-revisions/{id}"))
        {
            world.Admin(conditional.Headers);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", read.ETag);
            using var response = await world.Client.SendAsync(conditional, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        }

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/environment-revisions/{id}", TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg)))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/environment-revisions/{Guid.CreateVersion7()}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);

        // A CI credential holds no run:read (the service-role ceiling), so it cannot read a revision even of its own project.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/environment-revisions/{id}", TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"])))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Permissions_WriteNeedsEnvironmentWrite_ReadNeedsRunRead()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Config());

        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);
        (await CreateAsync(world, Config(postgres: "1"), caller: stranger)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/environment-revisions/{created.Id("revision_id")}", stranger)).Status.ShouldBe(HttpStatusCode.Forbidden);

        var reader = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]);
        (await CreateAsync(world, Config(postgres: "2"), caller: reader)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EnvironmentRevision_UpdateDelete_Revoked_AndThePointerAndProjectKeys_HoldAtTheDatabase()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Config());
        (await CreateAsync(world, Config(), environment: "prod")).Status.ShouldBe(HttpStatusCode.Created);

        foreach (var statement in new[]
        {
            "update public.environment_revision set dependency_versions = '{}'::jsonb",
            "delete from public.environment_revision",
        })
        {
            await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("org", world.Org.ToString());
                await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            await using var command = new NpgsqlCommand(statement, connection, transaction);
            (await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)))
                .SqlState.ShouldBe("42501", statement);
            (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, statement)))
                .MessageText.ShouldContain("revision_immutable", customMessage: statement);
        }

        // The pointer can only name one of the environment's own revisions.
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"update public.environment set current_revision_id = '{created.Id("revision_id")}' where environment_key = 'prod'")))
            .MessageText.ShouldContain("environment_pointer_invalid");
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            "update public.environment set environment_key = 'renamed' where environment_key = 'prod'")))
            .MessageText.ShouldContain("environment_identity_immutable");

        // project.default_environment_id carries the composite FK (AC6): an unknown environment is refused, a real one taken.
        (await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"update public.project set default_environment_id = '{Guid.CreateVersion7()}' where id = '{world.Project}'")))
            .ConstraintName.ShouldBe("project_default_env_fk");
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.project set default_environment_id = (select id from public.environment where environment_key = 'staging') where id = '{world.Project}'");
    }
}

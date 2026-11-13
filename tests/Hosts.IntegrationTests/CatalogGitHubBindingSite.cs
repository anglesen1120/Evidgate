using System.Globalization;
using System.Net.Http.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Npgsql;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// What a site seeds beyond the caller and their project: whether the App is configured, whether the installation
/// exists, and whether another tenant holds the repository or the installation.
/// </summary>
internal sealed record CatalogGitHubBindingSetup
{
    /// <summary>True when the host must consider a GitHub App configured; false proves the 503.</summary>
    internal bool GitHubConfigured { get; init; } = true;

    /// <summary>False leaves the org without any installation, so ownership cannot be proven.</summary>
    internal bool SeedInstallation { get; init; } = true;

    /// <summary>Seeds the reported installation under another org, the foreign-installation refusal.</summary>
    internal bool InstallationOwnedByAnotherOrg { get; init; }

    /// <summary>
    /// Seeds a binding for the same installation and repository under another project of the same org, the global-key
    /// conflict: an installation belongs to one org, so no other org can hold a binding in it.
    /// </summary>
    internal bool RepositoryBoundByAnotherProject { get; init; }
}

/// <summary>
/// One migrated database with the caller, their org and project, an optional installation and a booted Api host on
/// the real route: the registration route, its pipeline, its tenant scope and its writes are the ones under test.
/// </summary>
internal sealed class CatalogGitHubBindingSite : IAsyncDisposable
{
    /// <summary>The installation every seeded site reports; each site has its own database.</summary>
    internal const long InstallationNumber = 900001;

    /// <summary>The repository every request names; each site has its own database.</summary>
    internal const long RepositoryNumber = 5001;

    private readonly TestDatabase _database;
    private readonly CatalogGitHubBindingWorld _world;

    private CatalogGitHubBindingSite(
        TestDatabase database,
        CatalogGitHubBindingWorld world,
        EvidgateApiFactory factory,
        HttpClient client,
        CatalogGitHubHttpHandler gitHub)
    {
        _database = database;
        _world = world;
        Factory = factory;
        Client = client;
        GitHub = gitHub;
    }

    internal EvidgateApiFactory Factory { get; }

    internal HttpClient Client { get; }

    /// <summary>The GitHub handler the host's two clients resolve; its request log is what "no call" means.</summary>
    internal CatalogGitHubHttpHandler GitHub { get; }

    internal Guid User => _world.User;

    internal Guid Org => _world.Org;

    internal Guid Project => _world.Project;

    internal Guid OtherOrg => _world.OtherOrg;

    internal Guid OtherProject => _world.OtherProject;

    internal long InstallationId => _world.InstallationId;

    internal string Admin { get; private init; } = string.Empty;

    /// <summary>
    /// Migrates a database, seeds the two tenants and boots the Api host with the GitHub pipe replaced by the test
    /// handler. The membership is seeded because the tenant stage resolves it before any handler runs; the App is
    /// configured through the same in-memory configuration the host's secret provider reads.
    /// </summary>
    internal static async Task<CatalogGitHubBindingSite> StartAsync(
        PostgresFixture postgres,
        CatalogGitHubBindingSetup? setup = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        setup ??= new CatalogGitHubBindingSetup();
        var database = await postgres.CreateMigratedDatabaseAsync().ConfigureAwait(false);
        var admin = postgres.AdminConnectionString(database.Name);
        var world = await CatalogGitHubBindingSeed.SeedAsync(admin, setup, cancellationToken).ConfigureAwait(false);
        var gitHub = new CatalogGitHubHttpHandler();
        var factory = new EvidgateApiFactory(
            database.AppConnectionString,
            configureServices: services =>
            {
                services.RemoveAll<IPermissionEvaluator>();
                services.AddSingleton<IPermissionEvaluator>(new GrantAllPermissionEvaluator());
                services.AddSingleton<ICallerAuthenticator>(new SeededMemberAuthenticator(world.User));
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CatalogGitHubHandlerFilter(gitHub));
            },
            extraConfiguration: CatalogGitHubApp.Settings(setup.GitHubConfigured));

        return new CatalogGitHubBindingSite(database, world, factory, factory.CreateClient(), gitHub)
        {
            Admin = admin,
        };
    }

    /// <summary>POSTs the registration the way a client does: the org header, the key, the two numeric ids.</summary>
    internal Task<HttpResponseMessage> RegisterAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(idempotencyKey, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["installation_id"] = InstallationId,
            ["repository_id"] = RepositoryNumber,
        }, cancellationToken);

    /// <summary>POSTs an arbitrary body, so a request with an extra member is refused by name before the flow.</summary>
    internal Task<HttpResponseMessage> RegisterAsync(
        string idempotencyKey,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{Project}/git-repo-bindings"))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Evidgate-Org", Org.ToString("D"));
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return Client.SendAsync(request, cancellationToken);
    }

    /// <summary>Bindings of the calling org, read as the database's owner so RLS cannot hide a row.</summary>
    internal Task<long> BindingCountAsync() => CountAsync("git_repo_binding", Org);

    /// <summary>Audit rows of the calling org: the proof that a refusal wrote nothing.</summary>
    internal Task<long> AuditCountAsync() => CountAsync("audit_log", Org);

    /// <summary>Bindings of the calling project, so a refusal is proven to have written none.</summary>
    internal Task<long> ProjectBindingCountAsync() => CountAsync("git_repo_binding", Org, Project);

    /// <summary>Bindings of the other project, so a conflict is proven to leave its row untouched.</summary>
    internal Task<long> OtherProjectBindingCountAsync() => CountAsync("git_repo_binding", Org, OtherProject);

    /// <summary>The persisted secret reference of the calling project's binding, or null when none was written.</summary>
    internal Task<string?> PersistedSecretReferenceAsync() =>
        ScalarAsync<string?>(
            "select webhook_secret_ref from public.git_repo_binding where org_id = @org and project_id = @project",
            ("org", Org),
            ("project", Project));

    private async Task<long> CountAsync(string table, Guid org, Guid? project = null)
    {
        var sql = project is null
            ? string.Create(CultureInfo.InvariantCulture, $"select count(*) from public.{table} where org_id = @org")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"select count(*) from public.{table} where org_id = @org and project_id = @project");
        var count = project is null
            ? await ScalarAsync<long?>(sql, ("org", org)).ConfigureAwait(false)
            : await ScalarAsync<long?>(sql, ("org", org), ("project", project.Value)).ConfigureAwait(false);

        return count ?? 0;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return (T)result!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync().ConfigureAwait(false);
        await _database.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The ids one seeded site carries; the other tenant exists only for the cross-tenant refusals.</summary>
internal sealed record CatalogGitHubBindingWorld(
    Guid User,
    Guid Org,
    Guid Project,
    Guid OtherOrg,
    Guid OtherProject,
    long InstallationId);

/// <summary>The rows a host needs before its route can judge anything, inserted as the database's owner.</summary>
internal static class CatalogGitHubBindingSeed
{
    internal static async Task<CatalogGitHubBindingWorld> SeedAsync(
        string admin,
        CatalogGitHubBindingSetup setup,
        CancellationToken cancellationToken)
    {
        var user = Guid.CreateVersion7();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var otherOrg = Guid.CreateVersion7();
        var otherProject = Guid.CreateVersion7();
        var email = string.Create(CultureInfo.InvariantCulture, $"binding-{user:N}@example.test");
        var owner = setup.InstallationOwnedByAnotherOrg ? otherOrg : org;

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, "insert into public.\"user\" (id, email) values (@id, @email)", cancellationToken,
            ("id", user), ("email", email));
        await SeedOrgAsync(connection, org, "binding", cancellationToken);
        await SeedOrgAsync(connection, otherOrg, "other", cancellationToken);
        await ExecuteAsync(connection, "insert into public.project (id, org_id, name, slug) values (@id, @org, 'binding', @slug)", cancellationToken,
            ("id", project), ("org", org), ("slug", "binding-" + project.ToString("N")));
        await ExecuteAsync(connection, "insert into public.project (id, org_id, name, slug) values (@id, @org, 'other', @slug)", cancellationToken,
            ("id", otherProject), ("org", otherOrg), ("slug", "other-" + otherProject.ToString("N")));
        await ExecuteAsync(
            connection,
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org, @user, @email, 'active', now(), now(), 1)
            """,
            cancellationToken,
            ("id", Guid.CreateVersion7()), ("org", org), ("user", user), ("email", email));

        if (setup.SeedInstallation)
        {
            await ExecuteAsync(
                connection,
                """
                insert into public.github_installation
                    (id, org_id, installation_id, account_login, account_id, permissions, state, suspended_at)
                values (@id, @org, @installation_id, 'acme', 1, '{"metadata":"read"}'::jsonb, 'active', null)
                """,
                cancellationToken,
                ("id", Guid.CreateVersion7()), ("org", owner),
                ("installation_id", CatalogGitHubBindingSite.InstallationNumber));
        }

        if (setup.RepositoryBoundByAnotherProject)
        {
            await ExecuteAsync(
                connection,
                """
                insert into public.git_repo_binding
                    (id, org_id, project_id, installation_id, repository_id, default_branch, webhook_secret_ref)
                values (@id, @org, @project, @installation_id, @repository_id, 'main', @secret_ref)
                """,
                cancellationToken,
                ("id", Guid.CreateVersion7()), ("org", org), ("project", otherProject),
                ("installation_id", CatalogGitHubBindingSite.InstallationNumber.ToString(CultureInfo.InvariantCulture)),
                ("repository_id", CatalogGitHubBindingSite.RepositoryNumber.ToString(CultureInfo.InvariantCulture)),
                ("secret_ref", string.Create(CultureInfo.InvariantCulture, $"secret:gh_wh_{Guid.CreateVersion7():N}")));
        }

        return new CatalogGitHubBindingWorld(
            user, org, project, otherOrg, otherProject, CatalogGitHubBindingSite.InstallationNumber);
    }

    private static Task SeedOrgAsync(NpgsqlConnection connection, Guid org, string prefix, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, "insert into public.organization (id, name, slug) values (@id, @name, @slug)", cancellationToken,
            ("id", org), ("name", prefix), ("slug", prefix + "-" + org.ToString("N")));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

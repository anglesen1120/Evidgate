using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The arrangements of the install-route suite: the host that runs the real tenant path, the request helpers and the
/// owner-seam row fixtures. Kept apart from the cases so each file stays one screenful of intent.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    /// <summary>A start that succeeded, with the org, the state and the host that served it.</summary>
    private async Task<(IdentityApiFactory Host, HttpClient Client, FakeGitHubOAuth OAuth, FakeGitHubAppInstallations App, string State, Guid Org)>
        StartedAsync()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = UsableApp();

        var host = Host(org, admin, oauth, app);
        var client = host.CreateClient();

        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var state = StateOf((await ReadJsonAsync(start)).RootElement.GetProperty("url").GetString()!);

        return (host, client, oauth, app, state, org);
    }

    /// <summary>The App's own view of the probe installation, granting one permission.</summary>
    private FakeGitHubAppInstallations UsableApp() => new()
    {
        Installation = new GitHubAppInstallation(
            InstallationId,
            "acme",
            ProbeUserId,
            "User",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" }),
    };

    private Task<HttpResponseMessage> CallbackAsync(HttpClient client, string state) =>
        SendAsync(
            client,
            HttpMethod.Get,
            $"/v1/github/installations/callback?installation_id={InstallationId}&code=probe-code&state={state}",
            org: null);

    /// <summary>
    /// The host with the caller the module's pipeline authenticates: the tenant is NOT published, so the host's own
    /// filter has to resolve it — the property the browser callback depends on.
    /// </summary>
    private IdentityApiFactory Host(
        Guid org,
        Guid user,
        FakeGitHubOAuth oauth,
        FakeGitHubAppInstallations app,
        bool productionEvaluator = false) =>
        IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(user),
                Evaluator: productionEvaluator ? null : new GrantAllPermissionEvaluator(),
                ResolveTenant: true,
                Services: services =>
                {
                    services.AddSingleton(Options.Create(new GitHubOptions
                    {
                        AppId = "1",
                        ClientId = "Iv1.probe",
                        AppSlug = "evidgate-app",
                    }));
                    services.AddSingleton<IGitHubOAuthClient>(oauth);
                    services.AddSingleton<IGitHubAppInstallations>(app);
                }));

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        Guid? org,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (org is { } value)
        {
            request.Headers.Add(OrgHeader, value.ToString("D"));
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyHeader, idempotencyKey);
        }

        return client.SendAsync(request, Token);
    }

    private static string StateOf(string installUrl) =>
        new Uri(installUrl).Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "state")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .Single();

    /// <summary>The nonce part of a <c>&lt;org&gt;.&lt;nonce&gt;</c> state.</summary>
    private static string NonceOf(string state) => state[(state.IndexOf('.') + 1)..];

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).RootElement.GetProperty("error").GetProperty("code").GetString();

    private async Task<Guid> UserIdOfAsync(Guid org) =>
        await ScalarAsync<Guid>(
            "select user_id from public.membership where org_id = @org and status = 'active' limit 1",
            ("org", org));

    /// <summary>A second active membership for an existing user, so one caller belongs to two orgs.</summary>
    private async Task SeedMembershipAsync(Guid org, Guid userId) =>
        await Fixture.SeedAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org, @user, @email, 'active', now(), now(), 1)
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("org", org),
                ("user", userId),
                ("email", $"second-{userId:N}@example.test"),
            ],
            Token);

    private async Task<Guid> SeedInstallationAsync(Guid org, long installationId, string state)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            """
            insert into public.github_installation
                (id, org_id, installation_id, account_login, account_id, permissions, state, suspended_at)
            values (@id, @org, @installation_id, 'acme', 1, '{"metadata":"read"}'::jsonb, @state,
                    case when @state = 'suspended' then now() else null end)
            """,
            [("id", id), ("org", org), ("installation_id", installationId), ("state", state)],
            Token);

        return id;
    }

    private async Task InsertStateAsync(Guid org, Guid user, string nonce, DateTimeOffset expiresAt) =>
        await Fixture.SeedAsync(
            """
            insert into public.github_install_state (state_hash, org_id, user_id, expires_at)
            values (@hash, @org, @user, @expires_at)
            """,
            [
                ("hash", SHA256.HashData(Encoding.ASCII.GetBytes(nonce))),
                ("org", org),
                ("user", user),
                ("expires_at", expiresAt),
            ],
            Token);

    private async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] values)
    {
        var rows = await QueryAsync(sql, values);

        return rows.Count == 0 || rows[0] is not T value ? default : value;
    }

    private async Task<List<object?>> QueryAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(await reader.IsDBNullAsync(0, Token) ? null : reader.GetValue(0));
        }

        return rows;
    }

    private async Task<string> DumpAsync(string sql, params (string Name, object Value)[] values) =>
        await ScalarAsync<string>(sql, values) ?? string.Empty;
}

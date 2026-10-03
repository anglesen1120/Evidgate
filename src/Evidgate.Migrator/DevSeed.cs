using System.Globalization;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// <c>Evidgate.Migrator seed-dev</c> (01.8.1-T3.1): the minimal synthetic tenant that makes a fresh local stack usable
/// before login exists — one org, one project, a dev user with the <c>tenant_admin</c> and <c>release_approver</c> roles,
/// and one active CI binding for the dev repository. It runs as the owner role in ONE transaction and is idempotent
/// (fixed ids, <c>on conflict do nothing</c>), so running it twice leaves the same rows.
/// </summary>
/// <remarks>
/// Refuses, writing nothing, unless <c>DOTNET_ENVIRONMENT</c> is <c>Development</c> and the database host is the local
/// machine or a bare compose service name (no dots): a seed pointed at a shared database is the mistake this guard
/// exists for. The data is fully synthetic (00 §11). The dev ingest token and the sample policy are seed part 2
/// (01.8.1-T4), which needs 02.4.2 and 07.1.1.
/// </remarks>
public static class DevSeed
{
    /// <summary>The name of the environment variable that must equal <see cref="RequiredEnvironment"/>.</summary>
    public const string EnvironmentVariable = "DOTNET_ENVIRONMENT";

    /// <summary>The only environment the seed runs in.</summary>
    public const string RequiredEnvironment = "Development";

    /// <summary>Fixed ids: the natural keys of an idempotent seed.</summary>
    public static readonly Guid OrgId = Guid.Parse("0198a000-0000-7000-8000-00000000d001");

    /// <summary>The dev project.</summary>
    public static readonly Guid ProjectId = Guid.Parse("0198a000-0000-7000-8000-00000000d002");

    /// <summary>The dev user (no login exists yet; the id is what a session will carry).</summary>
    public static readonly Guid UserId = Guid.Parse("0198a000-0000-7000-8000-00000000d003");

    /// <summary>The dev user's membership.</summary>
    public static readonly Guid MembershipId = Guid.Parse("0198a000-0000-7000-8000-00000000d004");

    /// <summary>The dev CI binding.</summary>
    public static readonly Guid BindingId = Guid.Parse("0198a000-0000-7000-8000-00000000d005");

    /// <summary>The synthetic repository id of the dev binding.</summary>
    public const long DevRepositoryId = 9_000_000_000_001;

    private const string Sql =
        """
        insert into public.organization (id, name, slug)
        values (@org, 'Dev Org', 'dev-org')
        on conflict (id) do nothing;

        insert into public.project (id, org_id, name, slug)
        values (@project, @org, 'Dev Project', 'dev-project')
        on conflict (id) do nothing;

        insert into public.membership (id, org_id, user_id, invited_email, status)
        values (@membership, @org, @user, 'dev@evidgate.local', 'active')
        on conflict (id) do nothing;

        insert into public.membership_role (id, org_id, membership_id, role_id)
        select case r.code when 'tenant_admin' then @grant_admin else @grant_approver end, @org, @membership, r.id
          from public.role r
         where r.org_id is null and r.code in ('tenant_admin', 'release_approver')
        on conflict (id) do nothing;

        insert into public.ci_binding (id, org_id, project_id, provider, repository_id, repository_full_name,
                                       allowed_ref_patterns, allowed_workflow_refs, status, activated_at)
        values (@binding, @org, @project, 'github_actions', @repository, 'evidgate-dev/sample',
                array['refs/heads/*', 'refs/tags/*'],
                array['evidgate-dev/sample/.github/workflows/*@refs/heads/*'],
                'active', now())
        on conflict (id) do nothing;
        """;

    /// <summary>Applies the seed; returns the process exit code.</summary>
    /// <param name="options">Owner-role connection and paths.</param>
    /// <param name="environment">Environment overrides (tests); null reads the process environment.</param>
    /// <param name="output">Where progress goes.</param>
    /// <param name="error">Where a refusal or failure goes.</param>
    public static int Run(
        MigratorOptions options,
        IReadOnlyDictionary<string, string?>? environment,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var current = environment is not null
            ? (environment.TryGetValue(EnvironmentVariable, out var value) ? value : null)
            : Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (!string.Equals(current, RequiredEnvironment, StringComparison.Ordinal))
        {
            error.WriteLine($"E seed-dev refused: {EnvironmentVariable} must be '{RequiredEnvironment}' (it is '{current ?? "<unset>"}'); nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }

        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        if (!IsLocalHost(builder.Host))
        {
            error.WriteLine($"E seed-dev refused: the database host '{builder.Host}' is not local or a compose service name; nothing was written");

            return MigratorExitCodes.InvalidInvocation;
        }

        try
        {
            using var connection = new NpgsqlConnection(options.ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = new NpgsqlCommand(Sql, connection, transaction);
            command.Parameters.AddWithValue("org", OrgId);
            command.Parameters.AddWithValue("project", ProjectId);
            command.Parameters.AddWithValue("user", UserId);
            command.Parameters.AddWithValue("membership", MembershipId);
            command.Parameters.AddWithValue("binding", BindingId);
            command.Parameters.AddWithValue("repository", DevRepositoryId);
            command.Parameters.AddWithValue("grant_admin", Guid.Parse("0198a000-0000-7000-8000-00000000d006"));
            command.Parameters.AddWithValue("grant_approver", Guid.Parse("0198a000-0000-7000-8000-00000000d007"));
            var changed = command.ExecuteNonQuery();
            transaction.Commit();

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"seed-dev: org dev-org / project dev-project / user {UserId} / binding for repository {DevRepositoryId} ({changed} row(s) written; 0 means already seeded)"));

            return MigratorExitCodes.Success;
        }
        catch (NpgsqlException exception)
        {
            error.WriteLine("E seed-dev failed: " + SecretRedactor.Redact(exception.Message, SecretRedactor.PasswordOf(options.ConnectionString)));

            return exception.IsTransient || exception.InnerException is System.Net.Sockets.SocketException
                ? MigratorExitCodes.DatabaseUnavailable
                : MigratorExitCodes.MigrationFailed;
        }
    }

    /// <summary>Compose service names the dev stack uses for its database.</summary>
    private static readonly string[] ComposeHosts = ["postgres", "db"];

    /// <summary>
    /// Loopback, <c>localhost</c>, a unix-socket directory, or a known compose service name. Multi-host strings
    /// (<c>a,b</c>) and any other bare name are refused: an alias, tunnel or search-domain name may reach a shared database.
    /// </summary>
    /// <param name="host">The host of the connection string.</param>
    public static bool IsLocalHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && !host.Contains(',', StringComparison.Ordinal)
        && !host.Contains(';', StringComparison.Ordinal)
        && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith('/')
            || ComposeHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address)));
}

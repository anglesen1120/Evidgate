using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using Shouldly;
using Xunit;
using static Persistence.IntegrationTests.GitSchemaRows;

namespace Persistence.IntegrationTests;

/// <summary>
/// The installation-binding schema as the migration leaves it: forced row level security on both tables, the
/// composite tenant key, one live installation per org, the closed state machine with <c>deleted</c> terminal,
/// the single-use OAuth nonce, and the pre-tenant resolver that answers only the documented tuple.
/// </summary>
/// <remarks>
/// Rows are arranged over the owner connection, whose permissive policy is the one arrangement path a test may
/// use; the app role's reads are asserted on its own connection so a zero is a policy outcome, not an empty table.
/// </remarks>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class GitHubInstallationSchemaTests
{
    private readonly PostgresPersistenceFixture _fixture;

    public GitHubInstallationSchemaTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<NpgsqlConnection> OpenOwnerAsync(string database) =>
        GitSchemaRows.OpenOwnerAsync(_fixture.OwnerConnectionString(database));

    /// <summary>The two tables this task's migration creates in the tenant schema, in name order.</summary>
    private static readonly string[] TenantTables = ["github_install_state", "github_installation"];

    private const string InsertInstallSql =
        "insert into public.github_installation (id, org_id, installation_id, account_login, account_id, permissions, state) "
        + "values (@id, @org_id, @installation_id, 'acme', 42, '{\"checks\":\"write\"}'::jsonb, @state)";

    private const string InsertStateSql =
        "insert into public.github_install_state (state_hash, org_id, user_id, expires_at) "
        + "values (@state_hash, @org_id, @user_id, @expires_at)";

    /// <summary>
    /// Both tables are ENABLE + FORCE row level security with one policy per role, and each app policy reads the
    /// tenant setting — a policy without <c>TO</c> or an ENABLE without FORCE would widen the boundary.
    /// </summary>
    [Fact]
    public async Task Tables_CarryForcedRls_WithOnePolicyPerRole()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select c.relname, c.relrowsecurity, c.relforcerowsecurity
                  from pg_catalog.pg_class c
                  join pg_catalog.pg_namespace n on n.oid = c.relnamespace
                 where n.nspname = 'public'
                   and c.relname in ('github_installation', 'github_install_state')
                 order by c.relname
                """;
            await using var reader = await command.ExecuteReaderAsync(Token);
            var seen = new List<string>();
            while (await reader.ReadAsync(Token))
            {
                seen.Add(reader.GetString(0));
                reader.GetBoolean(1).ShouldBeTrue($"{reader.GetString(0)}: row level security must be ENABLED");
                reader.GetBoolean(2).ShouldBeTrue($"{reader.GetString(0)}: row level security must be FORCED");
            }

            seen.ShouldBe(TenantTables);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                select tablename, policyname, array_to_string(roles, ','), coalesce(qual, '')
                  from pg_catalog.pg_policies
                 where schemaname = 'public'
                   and tablename in ('github_installation', 'github_install_state')
                 order by tablename, policyname
                """;
            await using var reader = await command.ExecuteReaderAsync(Token);
            var rows = new List<string>();
            while (await reader.ReadAsync(Token))
            {
                var roles = reader.GetString(2);
                roles.ShouldBeOneOf(["evidgate_app", "evidgate_owner"], $"{reader.GetString(0)}.{reader.GetString(1)}");
                if (string.Equals(roles, "evidgate_app", StringComparison.Ordinal))
                {
                    reader.GetString(3).ShouldContain("current_org_id", customMessage: $"{reader.GetString(1)} reads the tenant setting");
                }

                rows.Add($"{reader.GetString(0)}.{reader.GetString(1)}:{roles}");
            }

            rows.Count.ShouldBe(4, "one tenant policy and one owner policy per table");
        }
    }

    /// <summary>
    /// The composite tenant key every business table declares, the platform-wide installation identity and the
    /// partial one-live-installation key are the constraints the rows below rely on.
    /// </summary>
    [Fact]
    public async Task Keys_AreTheDocumentedOnes()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);

        var installationKeys = await KeyDefinitionsAsync(connection, "public.github_installation");
        installationKeys.ShouldContain("github_installation_org_id_key=UNIQUE (org_id, id)");
        installationKeys.ShouldContain("github_installation_installation_id_key=UNIQUE (installation_id)");
        installationKeys.Where(definition => definition.Contains("FOREIGN KEY", StringComparison.Ordinal))
            .Count().ShouldBe(2, "org and installed_by are the only references");

        var stateKeys = await KeyDefinitionsAsync(connection, "public.github_install_state");
        stateKeys.ShouldContain("github_install_state_org_id_key=UNIQUE (org_id, id)");
        stateKeys.Where(definition => definition.Contains("FOREIGN KEY", StringComparison.Ordinal))
            .Count().ShouldBe(2, "org and user are the only references");

        var liveKey = await ScalarAsync<string>(
            connection,
            """
            select pg_get_indexdef(indexrelid)
              from pg_catalog.pg_index
             where indrelid = 'public.github_installation'::regclass
               and indexrelid = 'public.github_installation_active_org_key'::regclass
            """);
        liveKey.ShouldContain("CREATE UNIQUE INDEX github_installation_active_org_key");
        liveKey.ShouldContain("(org_id)");
        liveKey.ShouldContain("deleted");
    }

    /// <summary>
    /// One live installation per org: a second <c>active</c> or <c>suspended</c> row for the same org is refused,
    /// a deleted row stops counting, and the installation id stays unique across every org.
    /// </summary>
    [Fact]
    public async Task LiveInstallation_IsUniquePerOrg_AndIdentityIsGlobal()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        await SeedWorldAsync(connection);

        var first = await InsertInstallAsync(connection, OrgA, 1001);
        await AssertRefusedAsync(
            connection,
            InsertInstallSql,
            PostgresErrorCodes.UniqueViolation,
            "github_installation_active_org_key",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("installation_id", 1002L), ("state", "suspended"));

        await ExecuteAsync(connection, "update public.github_installation set state = 'deleted' where id = @id", ("id", first));

        var replacement = await InsertInstallAsync(connection, OrgA, 1003);
        (await CountAsync(connection, "public.github_installation", replacement))
            .ShouldBe(1, "a deleted row frees the org for a reinstall");

        await AssertRefusedAsync(
            connection,
            InsertInstallSql,
            PostgresErrorCodes.UniqueViolation,
            "github_installation_installation_id_key",
            ("id", Guid.CreateVersion7()), ("org_id", OrgB), ("installation_id", 1003L), ("state", "active"));
    }

    /// <summary>
    /// The state machine of the plan: <c>active ↔ suspended</c> and <c>active|suspended → deleted</c>, with
    /// <c>deleted</c> terminal and the installation identity frozen for every later write.
    /// </summary>
    [Fact]
    public async Task StateGuard_RefusesIllegalTransitions_AndFreezesTheIdentity()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        await SeedWorldAsync(connection);
        var id = await InsertInstallAsync(connection, OrgA, 2001);

        (await ExecuteAsync(
            connection,
            "update public.github_installation set state = 'suspended', suspended_at = now() where id = @id",
            ("id", id))).ShouldBe(1, "active -> suspended is the webhook transition");

        (await ExecuteAsync(
            connection,
            "update public.github_installation set state = 'active', suspended_at = null where id = @id",
            ("id", id))).ShouldBe(1, "suspended -> active is the unsuspend transition");

        var frozen = await ErrorAsync(
            connection,
            "update public.github_installation set installation_id = 2002 where id = @id",
            ("id", id));
        frozen.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        frozen.MessageText.ShouldContain("identity is immutable");

        (await ExecuteAsync(
            connection,
            "update public.github_installation set state = 'deleted' where id = @id",
            ("id", id))).ShouldBe(1, "active -> deleted is the uninstall transition");

        var revived = await ErrorAsync(
            connection,
            "update public.github_installation set state = 'active' where id = @id",
            ("id", id));
        revived.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        revived.MessageText.ShouldContain("deleted");

        var direct = await ErrorAsync(
            connection,
            "update public.github_installation set state = 'suspended' where id = @id",
            ("id", id));
        direct.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    /// <summary>
    /// The OAuth nonce row is org-scoped for a direct read, is consumed exactly once, and freezes its tenant,
    /// actor and deadline — a replayed callback has nothing new to write.
    /// </summary>
    [Fact]
    public async Task InstallState_IsOrgScoped_AndConsumedOnce()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        await SeedWorldAsync(connection);
        var user = await InsertUserAsync(connection);
        var hash = Hash();
        await InsertStateAsync(connection, OrgA, user, hash, DateTimeOffset.UtcNow.AddMinutes(10));

        (await ExecuteAsync(
            connection,
            "update public.github_install_state set used_at = now() where state_hash = @hash",
            ("hash", hash))).ShouldBe(1, "the callback consumes the nonce");

        var replay = await ErrorAsync(
            connection,
            "update public.github_install_state set used_at = now() where state_hash = @hash",
            ("hash", hash));
        replay.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        replay.MessageText.ShouldContain("consumed only once");

        var other = Hash();
        await InsertStateAsync(connection, OrgA, user, other, DateTimeOffset.UtcNow.AddMinutes(10));
        var moved = await ErrorAsync(
            connection,
            "update public.github_install_state set expires_at = now() + interval '1 day' where state_hash = @hash",
            ("hash", other));
        moved.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);

        // The app role sees only its own org's nonce; with no tenant setting it sees none.
        await using (var app = new NpgsqlConnection(_fixture.AppConnectionString(database)))
        {
            await app.OpenAsync(Token);
            (await ScalarAsync<long>(app, "select count(*) from public.github_install_state"))
                .ShouldBe(0L, "an unset tenant setting must yield zero nonces");

            await ExecuteAsync(
                app,
                "select set_config('evidgate.org_id', @org, false)",
                ("org", OrgA.ToString("D", CultureInfo.InvariantCulture)));
            (await ScalarAsync<long>(app, "select count(*) from public.github_install_state")).ShouldBe(2L);
        }

        await using var otherOrg = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await otherOrg.OpenAsync(Token);
        await ExecuteAsync(
            otherOrg,
            "select set_config('evidgate.org_id', @org, false)",
            ("org", OrgB.ToString("D", CultureInfo.InvariantCulture)));
        (await ScalarAsync<long>(otherOrg, "select count(*) from public.github_install_state"))
            .ShouldBe(0L, "another tenant's nonce is invisible");
    }

    /// <summary>
    /// AC7 under the 02.9.1 suite's name: the app role sees no installation row with the tenant setting unset, org A
    /// sees exactly its own, and org B sees none — the row is invisible across tenants, not filtered by the API.
    /// </summary>
    [Fact]
    public async Task Isolation_GithubInstallation_CrossTenant_ZeroRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using (var owner = await OpenOwnerAsync(database))
        {
            await SeedWorldAsync(owner);
            await InsertInstallAsync(owner, OrgA, 4001);
        }

        await using var app = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await app.OpenAsync(Token);
        (await ScalarAsync<long>(app, "select count(*) from public.github_installation"))
            .ShouldBe(0L, "an unset tenant setting must yield zero installation rows");

        await ExecuteAsync(
            app,
            "select set_config('evidgate.org_id', @org, false)",
            ("org", OrgA.ToString("D", CultureInfo.InvariantCulture)));
        (await ScalarAsync<long>(app, "select count(*) from public.github_installation")).ShouldBe(1L);

        await ExecuteAsync(
            app,
            "select set_config('evidgate.org_id', @org, false)",
            ("org", OrgB.ToString("D", CultureInfo.InvariantCulture)));
        (await ScalarAsync<long>(app, "select count(*) from public.github_installation"))
            .ShouldBe(0L, "another tenant's installation must be invisible");
    }

    /// <summary>
    /// The pre-tenant resolver answers exactly <c>(org_id, status)</c> for the installation id the webhook names,
    /// and nothing for an unknown id — the receiver must not learn a table column.
    /// </summary>
    [Fact]
    public async Task Resolver_ReturnsOnlyTheDocumentedTuple()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using (var owner = await OpenOwnerAsync(database))
        {
            await SeedWorldAsync(owner);
            await InsertInstallAsync(owner, OrgA, 3001);
            var suspended = await InsertInstallAsync(owner, OrgB, 3002);
            await ExecuteAsync(
                owner,
                "update public.github_installation set state = 'suspended', suspended_at = now() where id = @id",
                ("id", suspended));
        }

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(Token);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "select * from evidgate_resolve.installation_by_id(@id)";
            command.Parameters.AddWithValue("id", 3001L);
            await using var reader = await command.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).ShouldBeTrue("the resolver returned no row for a bound installation");
            reader.FieldCount.ShouldBe(2);
            reader.GetName(0).ShouldBe("org_id");
            reader.GetName(1).ShouldBe("status");
            reader.GetGuid(0).ShouldBe(OrgA);
            reader.GetString(1).ShouldBe("active");
            (await reader.ReadAsync(Token)).ShouldBeFalse("installation_id is unique: a second row is a bug");
        }

        (await ScalarAsync<string>(
            connection,
            "select status from evidgate_resolve.installation_by_id(@id)",
            ("id", 3002L))).ShouldBe("suspended");

        (await ScalarAsync<long>(
            connection,
            "select count(*) from evidgate_resolve.installation_by_id(@id)",
            ("id", 999999L))).ShouldBe(0L);
    }

    private static byte[] Hash() => SHA256.HashData(Guid.NewGuid().ToByteArray());

    private static async Task<List<string>> KeyDefinitionsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select conname || '=' || pg_get_constraintdef(oid) from pg_catalog.pg_constraint "
            + "where conrelid = @table::regclass and contype in ('u', 'f')";
        command.Parameters.AddWithValue("table", table);

        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            definitions.Add(reader.GetString(0));
        }

        return definitions;
    }

    private static async Task<Guid> InsertUserAsync(NpgsqlConnection connection)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            "insert into public.\"user\" (id, email) values (@id, @email)",
            ("id", id),
            ("email", $"probe-{id:N}@example.test"));
        return id;
    }

    private static Task<Guid> InsertInstallAsync(NpgsqlConnection connection, Guid org, long installationId)
        => InsertInstallAsync(connection, org, installationId, "active");

    private static async Task<Guid> InsertInstallAsync(
        NpgsqlConnection connection,
        Guid org,
        long installationId,
        string state)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            InsertInstallSql,
            ("id", id),
            ("org_id", org),
            ("installation_id", installationId),
            ("state", state));
        return id;
    }

    private static Task<int> InsertStateAsync(
        NpgsqlConnection connection,
        Guid org,
        Guid user,
        byte[] hash,
        DateTimeOffset expiresAt) =>
        ExecuteAsync(
            connection,
            InsertStateSql,
            ("state_hash", hash),
            ("org_id", org),
            ("user_id", user),
            ("expires_at", expiresAt));

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }
}

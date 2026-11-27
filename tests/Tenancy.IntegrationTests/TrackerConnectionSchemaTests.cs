using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The database side of the tracker connection table: its state machine, checks, keys and grants, as the app role.</summary>
[Trait("Category", "Docker")]
public sealed class TrackerConnectionSchemaTests
{
    private const string InsertConnection = """
        insert into public.tracker_connection (id, org_id, project_id, provider, access_token_cipher, create_issue_enabled)
        values (@id, @org, @project, @provider, @cipher, @create)
        """;

    private readonly PostgresTenancyFixture _fixture;

    public TrackerConnectionSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Connection_ActiveToNeedsReauthAndBack_IsAllowed()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await LiveConnectionAsync(world);

        await using var scope = await AppAsync(world);
        (await scope.ExecuteAsync("update public.tracker_connection set state = 'needs_reauth' where id = @id", ("id", id))).ShouldBe(1);
        (await scope.ExecuteAsync("update public.tracker_connection set state = 'active' where id = @id", ("id", id))).ShouldBe(1);
        (await scope.ExecuteAsync("update public.tracker_connection set state = 'needs_reauth' where id = @id", ("id", id))).ShouldBe(1);
        (await scope.ExecuteAsync(Disconnect, ("id", id))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_Disconnected_CannotBecomeLiveOrHoldATokenAgain()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await LiveConnectionAsync(world);
        await using (var scope = await AppAsync(world))
        {
            (await scope.ExecuteAsync(Disconnect, ("id", id))).ShouldBe(1);
            await scope.CommitAsync();
        }

        foreach (var revive in new[]
        {
            "update public.tracker_connection set state = 'active', access_token_cipher = 'x', disconnected_at = null where id = @id",
            "update public.tracker_connection set state = 'needs_reauth', disconnected_at = null where id = @id",
            "update public.tracker_connection set access_token_cipher = 'x' where id = @id",
            "update public.tracker_connection set granted_scopes = 'read' where id = @id",
        })
        {
            await using var scope = await AppAsync(world);
            (await scope.CaptureErrorAsync(revive, ("id", id))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        // Positive control: the same statements are accepted on a live connection of the project.
        var live = await LiveConnectionAsync(world, "linear");
        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync("update public.tracker_connection set access_token_cipher = 'x' where id = @id", ("id", live))).ShouldBe(1);
        (await accepted.ExecuteAsync("update public.tracker_connection set granted_scopes = 'read' where id = @id", ("id", live))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_DisconnectedRowHoldingACipher_IsRefusedByTheCheck()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await LiveConnectionAsync(world);

        await using (var scope = await AppAsync(world))
        {
            var error = await scope.CaptureErrorAsync(
                "update public.tracker_connection set state = 'disconnected', disconnected_at = now() where id = @id",
                ("id", id));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("tracker_connection_disconnected_clean_check");
        }

        // Positive control: the same disconnect with both ciphers cleared is accepted.
        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(Disconnect, ("id", id))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_NewRowWithCreateIssueEnabled_IsRefused()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync(InsertConnection, InsertArguments(world, "jira_cloud", "cipher", true)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(InsertConnection, InsertArguments(world, "jira_cloud", "cipher", false))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_ActiveWithoutAnAccessToken_IsRefusedByTheCheck()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        await using (var scope = await AppAsync(world))
        {
            var error = await scope.CaptureErrorAsync(
                "insert into public.tracker_connection (id, org_id, project_id, provider) values (@id, @org, @project, 'linear')",
                ("id", Guid.CreateVersion7()),
                ("org", world.Org),
                ("project", world.Project));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("tracker_connection_active_token_check");
        }

        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(InsertConnection, InsertArguments(world, "linear", "cipher", false))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_AppRole_CannotMoveCreateIssueEnabledOrAnIdentityColumn()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await LiveConnectionAsync(world);

        foreach (var column in new[] { "create_issue_enabled = true", "provider = 'linear'", "project_id = project_id", "org_id = org_id" })
        {
            await using var scope = await AppAsync(world);
            (await scope.CaptureErrorAsync($"update public.tracker_connection set {column} where id = @id", ("id", id)))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync("update public.tracker_connection set granted_scopes = 'read' where id = @id", ("id", id))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_IdentityColumns_AreImmutableEvenForTheOwner()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await LiveConnectionAsync(world);

        foreach (var change in new[] { "provider = 'linear'", $"project_id = '{world.OtherProject:D}'", "created_at = created_at - interval '1 day'" })
        {
            await using var scope = await OwnerAsync(world);
            (await scope.CaptureErrorAsync($"update public.tracker_connection set {change} where id = @id", ("id", id)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using var accepted = await OwnerAsync(world);
        (await accepted.ExecuteAsync("update public.tracker_connection set granted_scopes = 'read' where id = @id", ("id", id))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_UpstreamResourceId_IsSetOnce()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await LiveConnectionAsync(world);

        await using (var scope = await AppAsync(world))
        {
            (await scope.ExecuteAsync("update public.tracker_connection set upstream_resource_id = 'cloud-1' where id = @id", ("id", id))).ShouldBe(1);
            (await scope.ExecuteAsync("update public.tracker_connection set upstream_resource_id = 'cloud-1' where id = @id", ("id", id))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using var other = await AppAsync(world);
        (await other.CaptureErrorAsync("update public.tracker_connection set upstream_resource_id = 'cloud-2' where id = @id", ("id", id)))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Connection_SecondLiveConnection_IsRefusedUntilTheFirstIsDisconnected()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var first = await LiveConnectionAsync(world);

        await using (var scope = await AppAsync(world))
        {
            var error = await scope.CaptureErrorAsync(InsertConnection, InsertArguments(world, "jira_cloud", "cipher", false));
            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("tracker_connection_live_uq");
        }

        // Positive controls: another provider, another project, and the same provider after a disconnect all insert.
        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(InsertConnection, InsertArguments(world, "linear", "cipher", false))).ShouldBe(1);
        (await accepted.ExecuteAsync(InsertConnection, InsertArguments(world, "jira_cloud", "cipher", false, world.OtherProject))).ShouldBe(1);
        (await accepted.ExecuteAsync(Disconnect, ("id", first))).ShouldBe(1);
        (await accepted.ExecuteAsync(InsertConnection, InsertArguments(world, "jira_cloud", "cipher", false))).ShouldBe(1);
    }

    [Fact]
    public async Task Connection_OfAnotherProjectOrTenant_CannotBeNamedByAnItem()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var connection = await LiveConnectionAsync(world);

        // Another project of the same tenant, and another tenant: the composite key refuses both.
        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync(InsertItem, ItemArguments(world.Org, world.OtherProject, connection)))
                .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using (var scope = await AppAsync(other))
        {
            (await scope.CaptureErrorAsync(InsertItem, ItemArguments(other.Org, other.Project, connection)))
                .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(InsertItem, ItemArguments(world.Org, world.Project, connection))).ShouldBe(1);
    }

    [Fact]
    public async Task Item_WithAnUnknownKindOrAMalformedHash_IsRefused()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var connection = await LiveConnectionAsync(world);

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync(InsertItem.Replace("'story'", "'task'", StringComparison.Ordinal), ItemArguments(world.Org, world.Project, connection)))
                .ConstraintName.ShouldBe("tracker_item_kind_check");
        }

        await using (var scope = await AppAsync(world))
        {
            var arguments = ItemArguments(world.Org, world.Project, connection);
            arguments[^1] = ("hash", "sha256:short");
            (await scope.CaptureErrorAsync(InsertItem, arguments)).ConstraintName.ShouldBe("tracker_item_content_hash_check");
        }

        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(InsertItem, ItemArguments(world.Org, world.Project, connection))).ShouldBe(1);
    }

    [Fact]
    public async Task Requirement_TrackerItemLink_IsPinnedToTheSameProjectAndTenant()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        await using var other = await RequirementTestWorld.StartAsync(_fixture);
        var requirement = await world.CreatedAsync(world.Create());
        var sameProject = await ItemAsync(world, world.Project, await LiveConnectionAsync(world));
        var otherProject = await ItemAsync(world, world.OtherProject, await LiveConnectionAsync(world, "jira_cloud", world.OtherProject));
        var otherTenant = await ItemAsync(other, other.Project, await LiveConnectionAsync(other));
        const string link = "update public.requirement set tracker_item_id = @item where id = @id";

        // The app role holds no UPDATE on the link column yet, so the keys are probed as the owner.
        foreach (var foreign in new[] { otherProject, otherTenant })
        {
            await using var scope = await OwnerAsync(world);
            (await scope.CaptureErrorAsync(link, ("item", foreign), ("id", requirement.RequirementId)))
                .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using var accepted = await OwnerAsync(world);
        (await accepted.ExecuteAsync(link, ("item", sameProject), ("id", requirement.RequirementId))).ShouldBe(1);
    }

    [Fact]
    public async Task OAuthState_ConsumedAt_MovesOnceAndNothingElseChanges()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var id = await ConsentAsync(world);

        await using (var scope = await AppAsync(world))
        {
            (await scope.ExecuteAsync("update public.tracker_oauth_state set consumed_at = now() where id = @id", ("id", id))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var again = await AppAsync(world))
        {
            (await again.CaptureErrorAsync("update public.tracker_oauth_state set consumed_at = now() where id = @id", ("id", id)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        var fresh = await ConsentAsync(world);
        await using (var scope = await OwnerAsync(world))
        {
            (await scope.CaptureErrorAsync("update public.tracker_oauth_state set redirect_uri = 'https://x.example.test/' where id = @id", ("id", fresh)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using var forbidden = await AppAsync(world);
        (await forbidden.CaptureErrorAsync("update public.tracker_oauth_state set expires_at = now() + interval '1 day' where id = @id", ("id", fresh)))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task OAuthState_CompletedAndCancelled_NeedConsumedFirst_ExcludeEachOther_AndAreSetOnce()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var untaken = await ConsentAsync(world);
        var completed = await ConsentAsync(world);
        var cancelled = await ConsentAsync(world);

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync("update public.tracker_oauth_state set completed_at = now() where id = @id", ("id", untaken)))
                .ConstraintName.ShouldBe("tracker_oauth_state_steps_check");
        }

        await using (var scope = await AppAsync(world))
        {
            (await scope.ExecuteAsync("update public.tracker_oauth_state set consumed_at = now(), completed_at = now() where id = @id", ("id", completed))).ShouldBe(1);
            (await scope.ExecuteAsync("update public.tracker_oauth_state set consumed_at = now(), cancelled_at = now() where id = @id", ("id", cancelled))).ShouldBe(1);
            await scope.CommitAsync();
        }

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync("update public.tracker_oauth_state set cancelled_at = now() where id = @id", ("id", completed)))
                .ConstraintName.ShouldBe("tracker_oauth_state_steps_check");
        }

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync("update public.tracker_oauth_state set cancelled_at = null where id = @id", ("id", cancelled)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync("update public.tracker_oauth_state set completed_at = now() + interval '1 second' where id = @id", ("id", completed)))
                .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
    }

    [Fact]
    public async Task OAuthState_DigestOfTheWrongLength_IsRefused()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        await using (var scope = await AppAsync(world))
        {
            (await scope.CaptureErrorAsync(InsertConsent, ConsentArguments(world, new byte[16])))
                .ConstraintName.ShouldBe("tracker_oauth_state_hash_check");
        }

        await using var accepted = await AppAsync(world);
        (await accepted.ExecuteAsync(InsertConsent, ConsentArguments(world, TrackerTestSupport.NewHash()))).ShouldBe(1);
    }

    [Fact]
    public async Task TrackerTables_AreForceRlsWithPoliciesBoundToARole()
    {
        var database = await _fixture.SharedDatabaseAsync();
        string[] tables = ["tracker_connection", "tracker_item", "tracker_oauth_state"];

        foreach (var table in tables)
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.{table}'::regclass")).ShouldBeTrue(table);
            (await _fixture.ColumnAsync<string>(
                database,
                $"select polname || ':' || array_to_string(array(select rolname from pg_roles where oid = any(polroles)), ',') from pg_policy where polrelid = 'public.{table}'::regclass order by polname"))
                .ShouldBe(new[] { $"{table}_owner_backfill:evidgate_owner", $"{table}_tenant:evidgate_app" });
        }
    }

    [Fact]
    public async Task TrackerTables_GiveTheAppRoleNoDelete_AndTheOperatorAndSweepRolesNothing()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in new[] { "tracker_connection", "tracker_item", "tracker_oauth_state" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "SELECT")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "INSERT")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "UPDATE")).ShouldBeFalse(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "DELETE")).ShouldBeFalse(table);

            foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
            {
                foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
                {
                    (await _fixture.HasTablePrivilegeAsync(database, role, table, privilege)).ShouldBeFalse($"{role} {privilege} {table}");
                }
            }
        }

        // Column-scoped: the state moves, the write opt-in and the identity do not.
        (await _fixture.ScalarAsync<bool>(database, ColumnUpdate("state"))).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(database, ColumnUpdate("create_issue_enabled"))).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(database, ColumnUpdate("provider"))).ShouldBeFalse();
    }

    private const string Disconnect = """
        update public.tracker_connection
           set state = 'disconnected', access_token_cipher = null, refresh_token_cipher = null, disconnected_at = now()
         where id = @id
        """;

    private const string InsertItem = """
        insert into public.tracker_item (id, org_id, project_id, connection_id, provider_id, provider_key, kind, content_hash)
        values (@id, @org, @project, @connection, @provider_id, 'PROBE-1', 'story', @hash)
        """;

    private const string InsertConsent = """
        insert into public.tracker_oauth_state (id, org_id, project_id, user_id, provider, state_hash, redirect_uri, expires_at)
        values (@id, @org, @project, @user, 'jira_cloud', @hash, 'https://app.example.test/callback', now() + interval '10 minutes')
        """;

    private static string ColumnUpdate(string column) =>
        $"select has_column_privilege('evidgate_app', 'public.tracker_connection', '{column}', 'UPDATE')";

    private static (string Name, object Value)[] InsertArguments(
        RequirementTestWorld world,
        string provider,
        string cipher,
        bool createIssue,
        Guid? project = null) =>
    [
        ("id", Guid.CreateVersion7()),
        ("org", world.Org),
        ("project", project ?? world.Project),
        ("provider", provider),
        ("cipher", cipher),
        ("create", createIssue),
    ];

    private static (string Name, object Value)[] ItemArguments(Guid org, Guid project, Guid connection) =>
    [
        ("id", Guid.CreateVersion7()),
        ("org", org),
        ("project", project),
        ("connection", connection),
        ("provider_id", Guid.NewGuid().ToString("N")),
        ("hash", "sha256:" + new string('a', 64)),
    ];

    private static (string Name, object Value)[] ConsentArguments(RequirementTestWorld world, byte[] hash) =>
    [
        ("id", Guid.CreateVersion7()),
        ("org", world.Org),
        ("project", world.Project),
        ("user", world.User),
        ("hash", hash),
    ];

    private Task<TenantScope> AppAsync(RequirementTestWorld world) =>
        TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

    private Task<TenantScope> OwnerAsync(RequirementTestWorld world) =>
        TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);

    /// <summary>A committed live connection written by the app role.</summary>
    private async Task<Guid> LiveConnectionAsync(RequirementTestWorld world, string provider = "jira_cloud", Guid? project = null)
    {
        var arguments = InsertArguments(world, provider, "cipher", false, project);
        await using var scope = await AppAsync(world);
        await scope.ExecuteAsync(InsertConnection, arguments);
        await scope.CommitAsync();

        return (Guid)arguments[0].Value;
    }

    private async Task<Guid> ItemAsync(RequirementTestWorld world, Guid project, Guid connection)
    {
        var arguments = ItemArguments(world.Org, project, connection);
        await using var scope = await AppAsync(world);
        await scope.ExecuteAsync(InsertItem, arguments);
        await scope.CommitAsync();

        return (Guid)arguments[0].Value;
    }

    private async Task<Guid> ConsentAsync(RequirementTestWorld world)
    {
        var arguments = ConsentArguments(world, TrackerTestSupport.NewHash());
        await using var scope = await AppAsync(world);
        await scope.ExecuteAsync(InsertConsent, arguments);
        await scope.CommitAsync();

        return (Guid)arguments[0].Value;
    }
}

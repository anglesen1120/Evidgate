using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;
using static Persistence.IntegrationTests.GitSchemaRows;

namespace Persistence.IntegrationTests;

/// <summary>
/// The commit-registration schema as the migration leaves it: forced row level security with one policy per
/// role, the composite tenant keys that pin a sync row to its own project and binding, the global delivery
/// identity, the state guards of the three state columns, the CHECKs that close each vocabulary, and the
/// column-scoped UPDATE the application role is limited to.
/// </summary>
/// <remarks>
/// Rows are arranged over the owner connection, whose permissive policy is the one arrangement path a test
/// may use; a trigger and a CHECK behave the same for every role, and the app role's own column grants are
/// asserted against the app connection directly.
/// </remarks>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class GitSchemaTests
{
    private readonly PostgresPersistenceFixture _fixture;

    private static readonly string[] PolicyRoles = ["evidgate_app", "evidgate_owner"];

    public GitSchemaTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<NpgsqlConnection> OpenOwnerAsync(string database) =>
        GitSchemaRows.OpenOwnerAsync(_fixture.OwnerConnectionString(database));

    /// <summary>
    /// Every one of the three tables is ENABLE + FORCE row level security, and every policy names exactly the
    /// one role it serves — a policy without <c>TO</c> would filter the operator's 0001 grant as well, and an
    /// ENABLE without FORCE would let the table owner bypass its own policy.
    /// </summary>
    [Fact]
    public async Task Tables_CarryForcedRls_WithOnePolicyPerRole()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);

        var flags = await ReadFlagsAsync(connection);
        flags.Count.ShouldBe(Tables.Length, "each table must be present in the migrated schema");
        foreach (var (name, enabled, forced) in flags)
        {
            enabled.ShouldBeTrue($"{name}: row level security must be ENABLED");
            forced.ShouldBeTrue($"{name}: row level security must be FORCED — the owner would bypass the policy");
        }

        var policies = await ReadPoliciesAsync(connection);
        policies.Count.ShouldBe(Tables.Length * 2, "one tenant policy and one owner policy per table");
        foreach (var (table, policy, roles, qual) in policies)
        {
            roles.ShouldNotBeNullOrEmpty($"{table}.{policy}: a policy without TO applies to every role");
            roles.ShouldBeOneOf(PolicyRoles, $"{table}.{policy} names {roles}");
            if (string.Equals(roles, "evidgate_app", StringComparison.Ordinal))
            {
                qual.ShouldContain("current_org_id", customMessage: $"{table}.{policy}: the tenant policy reads the tenant setting");
            }
        }
    }

    /// <summary>
    /// The composite tenant keys refuse a parent of another organization: a sync row filed under org B naming
    /// org A's project or binding, and a binding filed under org B naming org A's project. Each shape's valid
    /// same-org row is inserted first as the positive control.
    /// </summary>
    [Fact]
    public async Task CrossTenantParent_IsRejectedByTheCompositeFk()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        var (projectA, _, projectB) = await SeedWorldAsync(connection);

        var binding = await InsertBindingAsync(connection, OrgA, projectA);
        (await InsertBindingAsync(connection, OrgB, projectB)).ShouldNotBe(Guid.Empty);
        var sync = await InsertSyncAsync(connection, OrgA, projectA, binding, NewSha());
        (await CountAsync(connection, "public.catalog_sync", sync)).ShouldBe(1, "the same-org sync is the control");

        await AssertRefusedAsync(
            connection,
            "insert into public.git_repo_binding (id, org_id, project_id, installation_id, repository_id, webhook_secret_ref) "
            + "values (@id, @org_id, @project_id, '1', @repository_id, 'secret:probe')",
            PostgresErrorCodes.ForeignKeyViolation,
            "git_repo_binding_project_fk",
            ("id", Guid.CreateVersion7()), ("org_id", OrgB), ("project_id", projectA), ("repository_id", NewNumber()));

        await AssertRefusedAsync(
            connection, InsertSyncSql, PostgresErrorCodes.ForeignKeyViolation, "catalog_sync_project_fk",
            ("id", Guid.CreateVersion7()), ("org_id", OrgB), ("project_id", projectA),
            ("binding_id", binding), ("commit_sha", NewSha()));

        await AssertRefusedAsync(
            connection, InsertSyncSql, PostgresErrorCodes.ForeignKeyViolation, "catalog_sync_binding_fk",
            ("id", Guid.CreateVersion7()), ("org_id", OrgB), ("project_id", projectB),
            ("binding_id", binding), ("commit_sha", NewSha()));
    }

    /// <summary>
    /// Each unique key refuses its duplicate and nothing else: the binding identity is per project, the sync
    /// identity per binding and commit, and the delivery identity is <em>global</em> — a second org's row with
    /// the same delivery id must be refused, while a fresh id is a different row.
    /// </summary>
    [Fact]
    public async Task UniqueKeys_RefuseTheDocumentedDuplicates()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        var (projectA, sibling, _) = await SeedWorldAsync(connection);

        var binding = await InsertBindingAsync(connection, OrgA, projectA, repositoryId: "4242");
        await AssertRefusedAsync(
            connection,
            "insert into public.git_repo_binding (id, org_id, project_id, installation_id, repository_id, webhook_secret_ref) "
            + "values (@id, @org_id, @project_id, '2', '4242', 'secret:probe')",
            PostgresErrorCodes.UniqueViolation,
            "git_repo_binding_repository_key",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("project_id", projectA));

        (await InsertBindingAsync(connection, OrgA, sibling, repositoryId: "4242"))
            .ShouldNotBe(Guid.Empty, "the repository key is scoped to its project");

        var commit = NewSha();
        await InsertSyncAsync(connection, OrgA, projectA, binding, commit);
        await AssertRefusedAsync(
            connection, InsertSyncSql, PostgresErrorCodes.UniqueViolation, "catalog_sync_commit_key",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("project_id", projectA),
            ("binding_id", binding), ("commit_sha", commit));

        await InsertDeliveryAsync(connection, OrgA, "delivery-1");
        await AssertRefusedAsync(
            connection,
            "insert into public.webhook_delivery (id, org_id, provider, delivery_id, event, payload_hash) "
            + "values (@id, @org_id, 'github', 'delivery-1', 'push', @payload_hash)",
            PostgresErrorCodes.UniqueViolation,
            "webhook_delivery_identity_key",
            ("id", Guid.CreateVersion7()), ("org_id", OrgB), ("payload_hash", Digest('a')));

        (await InsertDeliveryAsync(connection, OrgB, "delivery-2"))
            .ShouldNotBe(Guid.Empty, "a fresh delivery id is accepted for another org");
    }

    /// <summary>
    /// <c>git_repo_binding.sync_state</c> moves between the two installation states, in both directions, and
    /// only the event columns move with it: the repository identity and the secret reference are written
    /// once, so a later rewrite is refused rather than silently re-pointing the binding.
    /// </summary>
    [Fact]
    public async Task BindingGuard_AllowsBothStates_AndFreezesTheIdentity()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        var (projectA, _, _) = await SeedWorldAsync(connection);
        var binding = await InsertBindingAsync(connection, OrgA, projectA);

        (await ExecuteAsync(
            connection,
            "update public.git_repo_binding set sync_state = 'disabled', head_commit = @head where id = @id",
            ("head", NewSha()),
            ("id", binding))).ShouldBe(1, "active -> disabled is the suspend event, and head_commit moves with it");

        (await ExecuteAsync(
            connection,
            "update public.git_repo_binding set sync_state = 'active', last_synced_commit = @synced where id = @id",
            ("synced", NewSha()),
            ("id", binding))).ShouldBe(1, "disabled -> active is the unsuspend event");

        var rewritten = await ErrorAsync(
            connection, "update public.git_repo_binding set repository_id = '9999' where id = @id", ("id", binding));
        rewritten.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        rewritten.MessageText.ShouldContain("carries its identity");

        var resecret = await ErrorAsync(
            connection,
            "update public.git_repo_binding set webhook_secret_ref = 'secret:other' where id = @id",
            ("id", binding));
        resecret.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        resecret.MessageText.ShouldContain("carries its identity");

        // The guard runs before the CHECK, so an unknown state is refused by the transition guard itself.
        var unknown = await ErrorAsync(
            connection, "update public.git_repo_binding set sync_state = 'paused' where id = @id", ("id", binding));
        unknown.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        unknown.MessageText.ShouldContain("cannot move");
    }

    /// <summary>
    /// <c>catalog_sync</c> follows queued → running → done | failed and failed → queued (the dead-letter
    /// replay). A jump the machine does not have, and any write to a done row, is refused.
    /// </summary>
    [Fact]
    public async Task CatalogSyncGuard_FollowsQueuedRunningDone_AndFreezesDone()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        var (projectA, _, _) = await SeedWorldAsync(connection);
        var binding = await InsertBindingAsync(connection, OrgA, projectA);

        var sync = await InsertSyncAsync(connection, OrgA, projectA, binding, NewSha());
        var jumped = await ErrorAsync(
            connection, "update public.catalog_sync set state = 'done' where id = @id", ("id", sync));
        jumped.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        jumped.MessageText.ShouldContain("cannot move");

        (await ExecuteAsync(
            connection,
            "update public.catalog_sync set state = 'running', started_at = now() where id = @id",
            ("id", sync))).ShouldBe(1, "queued -> running is the claim");

        (await ExecuteAsync(
            connection,
            "update public.catalog_sync set state = 'done', counts = '{\"cases\":2}'::jsonb, finished_at = now() where id = @id",
            ("id", sync))).ShouldBe(1, "running -> done carries the counts");

        var reopened = await ErrorAsync(
            connection, "update public.catalog_sync set state = 'running' where id = @id", ("id", sync));
        reopened.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        reopened.MessageText.ShouldContain("done");

        var recounted = await ErrorAsync(
            connection, "update public.catalog_sync set counts = '{\"cases\":3}'::jsonb where id = @id", ("id", sync));
        recounted.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        recounted.MessageText.ShouldContain("done");

        var failed = await InsertSyncAsync(connection, OrgA, projectA, binding, NewSha());
        await ExecuteAsync(connection, "update public.catalog_sync set state = 'running' where id = @id", ("id", failed));
        await ExecuteAsync(
            connection,
            "update public.catalog_sync set state = 'failed', error = 'github 503' where id = @id",
            ("id", failed));
        (await ExecuteAsync(
            connection,
            "update public.catalog_sync set state = 'queued', error = null where id = @id",
            ("id", failed))).ShouldBe(1, "failed -> queued is the dead-letter replay");
    }

    /// <summary>
    /// <c>webhook_delivery.state</c> is informational and forward only: accepted settles as duplicate or
    /// processed once, and the received payload itself never changes.
    /// </summary>
    [Fact]
    public async Task DeliveryGuard_MovesStateForwardOnce_AndKeepsThePayload()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        await SeedWorldAsync(connection);

        var processed = await InsertDeliveryAsync(connection, OrgA, "delivery-p");
        (await ExecuteAsync(
            connection,
            "update public.webhook_delivery set state = 'processed' where id = @id",
            ("id", processed))).ShouldBe(1, "accepted -> processed is the settled outcome");

        var again = await ErrorAsync(
            connection, "update public.webhook_delivery set state = 'duplicate' where id = @id", ("id", processed));
        again.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        again.MessageText.ShouldContain("cannot move");

        var duplicate = await InsertDeliveryAsync(connection, OrgA, "delivery-d");
        (await ExecuteAsync(
            connection,
            "update public.webhook_delivery set state = 'duplicate' where id = @id",
            ("id", duplicate))).ShouldBe(1, "accepted -> duplicate is the replay outcome");

        var rewrite = await ErrorAsync(
            connection,
            "update public.webhook_delivery set payload_hash = @hash where id = @id",
            ("hash", Digest('b')),
            ("id", duplicate));
        rewrite.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        rewrite.MessageText.ShouldContain("received payload");
    }

    /// <summary>
    /// Each closed vocabulary and bounded shape refuses a value outside it, and the constraint that answers is
    /// the documented one — so a row can never hold a state, a hash or a commit the handlers do not know.
    /// </summary>
    [Fact]
    public async Task Checks_RefuseValuesOutsideEachVocabulary()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await OpenOwnerAsync(database);
        var (projectA, _, _) = await SeedWorldAsync(connection);
        var binding = await InsertBindingAsync(connection, OrgA, projectA);

        await AssertRefusedAsync(
            connection,
            "insert into public.git_repo_binding (id, org_id, project_id, installation_id, repository_id, "
            + "webhook_secret_ref, sync_state) values (@id, @org_id, @project_id, '1', '7', 'secret:probe', 'paused')",
            PostgresErrorCodes.CheckViolation,
            "git_repo_binding_sync_state_check",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("project_id", projectA));

        await AssertRefusedAsync(
            connection,
            "insert into public.git_repo_binding (id, org_id, project_id, installation_id, repository_id, webhook_secret_ref) "
            + "values (@id, @org_id, @project_id, '1', '8', 'ghp_notAReference')",
            PostgresErrorCodes.CheckViolation,
            "git_repo_binding_webhook_secret_ref_check",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("project_id", projectA));

        await AssertRefusedAsync(
            connection,
            "insert into public.webhook_delivery (id, org_id, provider, delivery_id, event, payload_hash, state) "
            + "values (@id, @org_id, 'github', 'delivery-x', 'push', @payload_hash, 'skipped')",
            PostgresErrorCodes.CheckViolation,
            "webhook_delivery_state_check",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("payload_hash", Digest('a')));

        await AssertRefusedAsync(
            connection,
            "insert into public.webhook_delivery (id, org_id, provider, delivery_id, event, payload_hash) "
            + "values (@id, @org_id, 'github', 'delivery-y', 'push', 'not-a-digest')",
            PostgresErrorCodes.CheckViolation,
            "webhook_delivery_payload_hash_check",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA));

        await AssertRefusedAsync(
            connection,
            "insert into public.catalog_sync (id, org_id, project_id, binding_id, commit_sha, state) "
            + "values (@id, @org_id, @project_id, @binding_id, @commit_sha, 'cancelled')",
            PostgresErrorCodes.CheckViolation,
            "catalog_sync_state_check",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("project_id", projectA),
            ("binding_id", binding), ("commit_sha", NewSha()));

        await AssertRefusedAsync(
            connection,
            "insert into public.catalog_sync (id, org_id, project_id, binding_id, commit_sha) "
            + "values (@id, @org_id, @project_id, @binding_id, 'not-a-sha')",
            PostgresErrorCodes.CheckViolation,
            "catalog_sync_commit_sha_check",
            ("id", Guid.CreateVersion7()), ("org_id", OrgA), ("project_id", projectA), ("binding_id", binding));
    }

    /// <summary>
    /// The application role updates only the columns its commands own: the binding's event columns, the
    /// sync's state and result columns, and the delivery's state. A write to a column the migration froze —
    /// or a delete — is refused for lack of the privilege, not merely by the trigger.
    /// </summary>
    [Fact]
    public async Task AppRole_UpdatesOnlyTheEventAndStateColumns()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using (var owner = await OpenOwnerAsync(database))
        {
            var (projectA, _, _) = await SeedWorldAsync(owner);
            var binding = await InsertBindingAsync(owner, OrgA, projectA);
            await InsertSyncAsync(owner, OrgA, projectA, binding, NewSha());
            await InsertDeliveryAsync(owner, OrgA, "delivery-1");
        }

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(Token);
        await ExecuteAsync(
            connection,
            "select set_config('evidgate.org_id', @org, false)",
            ("org", OrgA.ToString("D", CultureInfo.InvariantCulture)));

        (await ExecuteAsync(
            connection,
            "update public.git_repo_binding set sync_state = 'disabled' where org_id = @org",
            ("org", OrgA))).ShouldBe(1, "the binding's event columns are the app role's to write");

        (await ExecuteAsync(
            connection,
            "update public.catalog_sync set state = 'running', started_at = now() where org_id = @org",
            ("org", OrgA))).ShouldBe(1, "the sync handler owns the state and result columns");

        (await ExecuteAsync(
            connection,
            "update public.webhook_delivery set state = 'processed' where org_id = @org",
            ("org", OrgA))).ShouldBe(1, "the delivery's informational state is the app role's to settle");

        foreach (var sql in new[]
                 {
                     "update public.git_repo_binding set repository_id = '9999' where org_id = @org",
                     "update public.catalog_sync set commit_sha = 'abcdef1' where org_id = @org",
                     "update public.webhook_delivery set event = 'ping' where org_id = @org",
                     "delete from public.catalog_sync where org_id = @org",
                 })
        {
            (await ErrorAsync(connection, sql, ("org", OrgA))).SqlState
                .ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{sql} is outside the app role's grant");
        }
    }
}

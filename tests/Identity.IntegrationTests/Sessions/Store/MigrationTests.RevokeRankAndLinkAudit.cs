using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// The head state of the revoke and link writers: the single-session revoke asks the rank hierarchy under the
/// target's user lock before it appends, the audited link reads its actor before it links, and the one chain
/// append refuses metadata the application writer's canonical hash cannot spell.
/// </summary>
public sealed partial class MigrationTests
{
    /// <summary>
    /// 0090 re-points the revoke definers at the shared helpers; 0091 re-states the revoke-all definer and the helper
    /// it asks; 0101 gives the single-session path the same rank question and delegates its append. Together they are
    /// the head state of these functions.
    /// </summary>
    private static readonly string[] HelperMigrationFiles =
    [
        "0090_identity_session_revoke_helpers.sql",
        "0091_identity_session_revoke_all_hierarchy.sql",
        "0101_identity_revoke_rank_and_atomic_link_audit.sql",
    ];

    /// <summary>The audited link definer's stored source, as the ordering assertion reads it.</summary>
    private const string AuditedLinkBodySql = """
        select p.prosrc
          from pg_catalog.pg_proc p
          join pg_catalog.pg_namespace n on n.oid = p.pronamespace
         where n.nspname = 'evidgate_resolve' and p.proname = 'login_link_identity_audited'
        """;

    /// <summary>Re-applies 0090, 0091 and 0101 so a case that re-stated an older body leaves the database at head.</summary>
    private async Task ApplyHelperMigrationAsync()
    {
        foreach (var file in HelperMigrationFiles)
        {
            await Fixture.SeedAsync(
                await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.MigrationsPath, file), Token),
                [],
                Token);
        }
    }

    /// <summary>
    /// The single-session revoke proves the rank again once the target's user lock is held — the order revoke-all
    /// takes — and the audited link reads its actor BEFORE the inner writer, so a credential killed during the
    /// link cannot leave a committed link whose rows name nobody.
    /// </summary>
    [Fact]
    public async Task The_Head_Writers_Lock_And_Read_Their_Actor_Before_They_Write()
    {
        await ApplyHelperMigrationAsync();

        var revokeBody = await OwnerScalarAsync<string>(RevokeBodySql);
        var userLock = revokeBody.IndexOf("evidgate.session_user_lock(", StringComparison.Ordinal);
        var recheck = revokeBody.IndexOf("session_admin_may_revoke_all", userLock, StringComparison.Ordinal);
        var update = revokeBody.IndexOf("update public.session", StringComparison.Ordinal);

        userLock.ShouldBeGreaterThan(-1, "the admin branch serialises on the target's user lock");
        recheck.ShouldBeGreaterThan(userLock, "the rank question is asked again once the lock is held");
        update.ShouldBeGreaterThan(recheck, "no session row is written before the re-check");
        revokeBody.ShouldContain("evidgate.session_revoke_audit_append", Case.Sensitive);

        var linkBody = await OwnerScalarAsync<string>(AuditedLinkBodySql);
        var actorRead = linkBody.IndexOf("from public.session s", StringComparison.Ordinal);
        var innerCall = linkBody.IndexOf("from evidgate_resolve.login_link_identity(", StringComparison.Ordinal);

        actorRead.ShouldBeGreaterThan(-1, "the audited definer resolves the acting user itself");
        innerCall.ShouldBeGreaterThan(actorRead, "the actor is read before the link commits");
        linkBody.ShouldContain("raise exception", Case.Sensitive,
            "a linked outcome without an actor aborts the call instead of committing an unattributed link");
    }

    /// <summary>
    /// The chain append rejects metadata the canonical hash mis-spells: an SQL NULL, a JSON null, an empty
    /// object, a JSON array, and any value that is not a JSON string, including a nested object. The one valid
    /// shape still appends a row.
    /// </summary>
    [Fact]
    public async Task The_Chain_Append_Refuses_Metadata_The_Canonical_Hash_Cannot_Spell()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var actor = Guid.CreateVersion7();

        string?[] refused =
        [
            null,
            "null",
            "{}",
            "[1]",
            """{"provider": null}""",
            """{"provider": 1}""",
            """{"nested": {"a": 1}}""",
        ];

        foreach (var metadata in refused)
        {
            var refusal = await OwnerRefusalAsync(
                AppendSql(metadata),
                ("org", org),
                ("actor", actor));

            refusal.SqlState.ShouldBe(PostgresErrorCodes.RaiseException, metadata ?? "SQL NULL");
            refusal.MessageText.ShouldContain("metadata");
        }

        await OwnerExecuteAsync(AppendSql("""{"provider": "github"}"""), ("org", org), ("actor", actor));

        (await OwnerScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = 'auth.test'",
            ("org", org))).ShouldBe(1L, "the one valid shape still appends exactly one row");
    }

    /// <summary>One call of the owner-only chain append; a null literal is the SQL NULL argument.</summary>
    private static string AppendSql(string? metadata) =>
        "select evidgate.audit_append_user_event("
        + "'auth.test', @org, @actor, 'user', @actor, "
        + (metadata is null ? "null::jsonb" : $"'{metadata}'::jsonb")
        + ", null, null)";
}

using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.3.1-T2 / migration 0044: <c>membership_user_fk</c> — the key 0011 left out and 0041:46-53 recorded
/// why (the invite path wrote a <c>user_id</c> before any <c>user</c> row existed). The column stays
/// nullable, a non-null value must now name a real account, and the migration refuses to validate over
/// orphan rows instead of deleting them (the 0031 <c>artifact_run_fk</c> precedent).
/// </summary>
/// <remarks>
/// The orphan case is exercised the way 0031's is: drop the key, seed the orphan the check guards
/// against, then re-run the migration file itself as the migration role and read the refusal. The
/// harness shares one database per assembly and its reset truncates rows but never DDL, so the test
/// restores both the key and the row in a <c>finally</c>; the assertion after it proves the restore.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class MembershipUserFkTests : SessionTestBase
{
    private const string MigrationFile = "0044_identity_login_linking.sql";

    public MembershipUserFkTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Done-when: a membership for an id no <c>user</c> row holds is refused by the key, by name.</summary>
    [Fact]
    public async Task ForeignKey_RefusesMembershipWithAnUnknownUser()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);

        var refused = await OwnerRefusalAsync(
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("user_id", Guid.CreateVersion7()),
            ("email", "ghost-member@example.test"));

        refused.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        refused.ConstraintName.ShouldBe("membership_user_fk");
    }

    /// <summary>
    /// The column stays nullable: a pending invite has no user yet, and NULL passes a MATCH SIMPLE key —
    /// which is exactly why 0011 could ship the column without the key.
    /// </summary>
    [Fact]
    public async Task NullUserId_IsStillAllowed()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membershipId = Guid.CreateVersion7();

        (await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, 'pending')",
            [
                ("id", membershipId),
                ("org_id", orgId),
                ("email", "pending-invite@example.test"),
            ],
            Token)).ShouldBe(1);

        (await OwnerScalarAsync<long>(
            "select count(*) from public.membership where id = @id and user_id is null",
            ("id", membershipId))).ShouldBe(1);
    }

    /// <summary>
    /// The key is in force (<c>convalidated</c>: the <c>validate constraint</c> step ran, not only the
    /// <c>not valid</c> add) and the parent-side index exists, so deleting a user does not scan every
    /// membership row.
    /// </summary>
    [Fact]
    public async Task TheKeyIsValidated_AndItsParentIndexExists()
    {
        (await OwnerScalarAsync<bool>(
            "select convalidated from pg_catalog.pg_constraint "
            + "where conrelid = 'public.membership'::regclass and conname = 'membership_user_fk'"))
            .ShouldBeTrue();

        var index = await OwnerScalarAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'membership_user_idx'");

        index.ShouldContain("(user_id)", Case.Insensitive);
    }

    /// <summary>
    /// The parent side of the key: NO ACTION, like every neighbour (0011, 0041) — deleting a user who still
    /// has a membership fails rather than taking the membership with it, because erasure is 11's job and
    /// decides its own order.
    /// </summary>
    [Fact]
    public async Task DeletingAUserWithAMembership_IsRefused()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await SeedUserAsync("keep-member@example.com");

        await OwnerExecuteAsync(
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("user_id", userId),
            ("email", "keep-member@example.com"));

        var refused = await OwnerRefusalAsync("delete from public.\"user\" where id = @id", ("id", userId));

        refused.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        refused.ConstraintName.ShouldBe("membership_user_fk");
        (await OwnerScalarAsync<long>("select count(*) from public.\"user\" where id = @id", ("id", userId)))
            .ShouldBe(1);
    }

    /// <summary>
    /// The file is idempotent: re-running it over a database that already has the key and a valid row
    /// changes neither — the existence check skips the add, the orphan count is zero and the validate is a
    /// no-op.
    /// </summary>
    [Fact]
    public async Task Migration_ReRun_KeepsAValidMembershipRow()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await SeedUserAsync("valid-member@example.com");
        var membershipId = Guid.CreateVersion7();

        await OwnerExecuteAsync(
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
            ("id", membershipId),
            ("org_id", orgId),
            ("user_id", userId),
            ("email", "valid-member@example.com"));

        await ReRunMigrationAsync();

        (await OwnerScalarAsync<long>("select count(*) from public.membership where id = @id", ("id", membershipId)))
            .ShouldBe(1);
        (await OwnerScalarAsync<bool>(
            "select convalidated from pg_catalog.pg_constraint "
            + "where conrelid = 'public.membership'::regclass and conname = 'membership_user_fk'"))
            .ShouldBeTrue();
    }

    /// <summary>
    /// The orphan check, and the message it must carry: a row whose <c>user_id</c> has no <c>user</c> row
    /// stops the migration with this key's name and an instruction — it is never deleted silently.
    /// </summary>
    [Fact]
    public async Task Migration_ReRun_WithAnOrphanMembership_FailsWithTheClearMessage()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var membershipId = Guid.CreateVersion7();

        // Drop the key so an orphan row can exist, exactly the state the orphan check guards.
        await OwnerExecuteAsync("alter table public.membership drop constraint membership_user_fk");

        try
        {
            await OwnerExecuteAsync(
                """
                insert into public.membership (id, org_id, user_id, invited_email, status)
                values (@id, @org_id, @user_id, @email, 'pending')
                """,
                ("id", membershipId),
                ("org_id", orgId),
                ("user_id", Guid.CreateVersion7()),
                ("email", "orphan-member@example.test"));

            var refused = await Should.ThrowAsync<PostgresException>(ReRunMigrationAsync);

            refused.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            refused.MessageText.ShouldContain("membership_user_fk");
            refused.MessageText.ShouldContain("no user row");
        }
        finally
        {
            // One database per assembly, and the reset empties rows only: the key this test dropped has to be
            // back before the next test runs, so the orphan goes first and the file re-adds the key.
            await OwnerExecuteAsync("delete from public.membership where id = @id", ("id", membershipId));
            await ReRunMigrationAsync();
        }

        (await OwnerScalarAsync<bool>(
            "select convalidated from pg_catalog.pg_constraint "
            + "where conrelid = 'public.membership'::regclass and conname = 'membership_user_fk'"))
            .ShouldBeTrue();
    }

    /// <summary>The migration file's own SQL, applied as the migration role (one implicit transaction).</summary>
    private async Task ReRunMigrationAsync()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = await MigrationSqlAsync();
        try
        {
            await command.ExecuteNonQueryAsync(Token);
        }
        finally
        {
            // The database is shared by the whole assembly: re-applying 0044 re-creates the read-only
            // `session_user` lookup that 0045 dropped, which a later session-migration test would then
            // see (order-dependent failure). Put 0045's state back.
            await OwnerExecuteAsync("drop function if exists evidgate_resolve.session_user(bytea, integer)");
        }
    }

    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(Path.Combine(DatabaseFixture.MigrationsPath, MigrationFile), Token);
}

using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The pre-check of <c>0097</c>: a database at head-minus-one that already holds a <c>membership_role</c> row
/// naming a staff role stops the migration with the row count and rewrites nothing; once the row is fixed the
/// same file applies.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class MembershipRoleKeyMigrationTests
{
    private const string KeyFile = "0097_identity_membership_role_fk.sql";

    private readonly PostgresMigratorFixture _fixture;

    public MembershipRoleKeyMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PreCheck_Raises_With_The_Count_And_Leaves_The_Row_Untouched_Then_Applies_Once_Fixed()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();

        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, KeyFile) < 0)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        var orgId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();
        var grantId = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            "insert into public.organization (id, slug, name) values (@org, 'precheck', 'precheck'); "
            + "insert into public.membership (id, org_id, invited_email, status) values (@m, @org, 'a@example.com', 'pending'); "
            + "insert into public.membership_role (id, org_id, membership_id, role_id) "
            + "select @g, @org, @m, id from public.role where code = 'platform_operator' and org_id is null",
            [("org", orgId), ("m", membershipId), ("g", grantId)],
            token);

        migrations.CopyReal(KeyFile);
        var refused = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        refused.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        refused.Transcript.ShouldContain("1 existing row(s)");

        (await ScalarAsync(database, "select count(*) from public.membership_role where id = @g", [("g", grantId)], token))
            .ShouldBe(1, "the pre-check never deletes or rewrites the violating row");
        (await ScalarAsync(
            database,
            "select count(*) from pg_constraint where conname = 'membership_role_role_fk'",
            [],
            token)).ShouldBe(0, "the failed file rolled back with its key");

        await ExecuteAsync(
            database,
            "update public.membership_role set role_id = (select id from public.role where code = 'viewer' and org_id is null) where id = @g",
            [("g", grantId)],
            token);

        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await ScalarAsync(
            database,
            "select count(*) from pg_constraint where conname = 'membership_role_role_fk' and convalidated",
            [],
            token)).ShouldBe(1);
    }

    private async Task ExecuteAsync(
        string database,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<long> ScalarAsync(
        string database,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(token))!;
    }
}

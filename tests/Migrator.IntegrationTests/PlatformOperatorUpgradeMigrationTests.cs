using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// <c>0105_platform_operator_role.sql</c> applied onto a database that still holds 0052's EXECUTE grant for the app role:
/// the upgrade path a fresh migrate-to-head never exercises, because there 0052 and 0105 run in one pass.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class PlatformOperatorUpgradeMigrationTests
{
    private const string OperatorFile = "0105_platform_operator_role.sql";

    private const string Definer = "evidgate.healing_switch_set_system(text, text, uuid)";

    private readonly PostgresMigratorFixture _fixture;

    public PlatformOperatorUpgradeMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    private static string CanExecute(string role) =>
        $"select has_function_privilege('{role}', '{Definer}', 'EXECUTE')";

    [Fact]
    public async Task Migration_OverAnExistingAppGrant_RevokesItAndMovesExecuteToTheOperatorRole()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create();

        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, OperatorFile) < 0)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        (await _fixture.ScalarAsync<bool>(database, CanExecute("evidgate_app")))
            .ShouldBeTrue("0052 granted EXECUTE to the app role; the upgrade starts from that state");
        (await _fixture.TableExistsAsync(database, "platform_operator_audit")).ShouldBeFalse();

        migrations.CopyReal(OperatorFile);
        var applied = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([OperatorFile], "nothing before it is re-applied");

        (await _fixture.ScalarAsync<bool>(database, CanExecute("evidgate_app"))).ShouldBeFalse("the app grant is revoked");
        (await _fixture.ScalarAsync<bool>(database, CanExecute("evidgate_platform_operator"))).ShouldBeTrue();
        foreach (var role in new[] { "evidgate_operator", "evidgate_worker", "evidgate_worker_sched" })
        {
            (await _fixture.ScalarAsync<bool>(database, CanExecute(role))).ShouldBeFalse(role);
        }

        (await _fixture.TableExistsAsync(database, "platform_operator_audit")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_table_privilege('evidgate_app', 'public.platform_operator_audit', 'SELECT')"))
            .ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select has_table_privilege('evidgate_operator', 'public.platform_operator_audit', 'SELECT')"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_trigger where not tgisinternal and tgname = 'platform_operator_audit_append_only'"))
            .ShouldBe(1L);

        var again = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBeEmpty();
    }
}

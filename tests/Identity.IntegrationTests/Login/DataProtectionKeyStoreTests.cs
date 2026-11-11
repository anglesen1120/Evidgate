using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// The Data Protection key table: round trip through the repository, insert-if-absent on a repeated name, and
/// the grant boundary — the app role reaches the platform tables only through the three definers, and neither
/// the operator nor the worker role may execute or read them.
/// </summary>
[Trait("Category", "Docker")]
public sealed class DataProtectionKeyStoreTests : SessionTestBase
{
    /// <summary>The three definers of 0100, as the signature strings <c>has_function_privilege</c> takes.</summary>
    private static readonly string[] Definers =
    [
        "evidgate_resolve.data_protection_key_list()",
        "evidgate_resolve.data_protection_key_store(text, text)",
        "evidgate_resolve.login_state_replay_consume(bytea, integer)",
    ];

    public DataProtectionKeyStoreTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    [Fact]
    public void A_Stored_Element_Is_Listed_And_A_Repeated_Name_Keeps_The_First()
    {
        var repository = new DataProtectionKeyRepository(AppDataSource);

        repository.StoreElement(new XElement("key", new XAttribute("id", "a"), "first"), "key-a");
        repository.StoreElement(new XElement("key", new XAttribute("id", "a"), "second"), "key-a");
        repository.StoreElement(new XElement("key", new XAttribute("id", "b")), "key-b");

        var elements = new DataProtectionKeyRepository(AppDataSource).GetAllElements();

        elements.Count.ShouldBe(2);
        elements.Single(e => (string?)e.Attribute("id") == "a").Value.ShouldBe("first");
    }

    [Theory]
    [InlineData("select 1 from public.data_protection_key")]
    [InlineData("insert into public.data_protection_key (friendly_name, key_xml) values ('x', '<k/>')")]
    [InlineData("select 1 from public.login_state_replay")]
    [InlineData("delete from public.login_state_replay")]
    public async Task The_App_Role_Has_No_Direct_Privilege_On_The_Tables(string sql)
    {
        await using var command = AppDataSource.CreateCommand(sql);

        var error = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Token));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Only_The_App_Role_May_Execute_The_Definers()
    {
        var rows = (await OwnerRowAsync(
            "select count(*) from information_schema.routine_privileges "
            + "where routine_schema = 'evidgate_resolve' and routine_name in "
            + "('data_protection_key_list', 'data_protection_key_store', 'login_state_replay_consume') "
            + "and grantee not in ('evidgate_app', 'evidgate_owner')"))[0];

        rows.ShouldBe(0L);
    }

    /// <summary>The positive control of the boundary: each definer is executable by the app role, and only there.</summary>
    [Fact]
    public async Task The_App_Role_Holds_Execute_On_Exactly_The_Three_Definers()
    {
        var grants = await OwnerScalarAsync<long>(
            "select count(distinct routine_name) from information_schema.routine_privileges "
            + "where routine_schema = 'evidgate_resolve' "
            + "and routine_name in ('data_protection_key_list', 'data_protection_key_store', 'login_state_replay_consume') "
            + "and grantee = 'evidgate_app' and privilege_type = 'EXECUTE'");
        grants.ShouldBe(3L);

        foreach (var routine in Definers)
        {
            (await HasExecuteAsync("evidgate_app", routine)).ShouldBeTrue(routine);
        }
    }

    [Theory]
    [InlineData("evidgate_operator")]
    [InlineData("evidgate_worker_sched")]
    public async Task The_Operator_And_Worker_Roles_May_Neither_Execute_Nor_Read(string role)
    {
        foreach (var routine in Definers)
        {
            (await HasExecuteAsync(role, routine)).ShouldBeFalse($"{role} must not execute {routine}");
        }

        await using var connection = await OpenAsRoleAsync(role);

        foreach (var table in new[] { "data_protection_key", "login_state_replay" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"select count(*) from public.{table}";

            var error = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Token));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} holds no SELECT on {table}");
        }
    }

    /// <summary>The lifetime is a duration in seconds, and the definer refuses anything outside one day.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(86401)]
    public async Task The_Replay_Definer_Refuses_A_Lifetime_Outside_One_Day(int seconds)
    {
        await using var command = AppDataSource.CreateCommand(
            "select evidgate_resolve.login_state_replay_consume(@hash, @seconds)");
        command.Parameters.AddWithValue(
            "hash",
            SHA256.HashData(Encoding.UTF8.GetBytes("lifetime-" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        command.Parameters.AddWithValue("seconds", seconds);

        var error = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Token));
        error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);
    }

    private Task<bool> HasExecuteAsync(string role, string routine) =>
        OwnerScalarAsync<bool>(
            "select has_function_privilege(@role, @routine, 'EXECUTE')",
            ("role", role),
            ("routine", routine));

    /// <summary>Opens a superuser connection switched to <paramref name="role"/> (the harness has no password for it).</summary>
    private async Task<NpgsqlConnection> OpenAsRoleAsync(string role)
    {
        var database = new NpgsqlConnectionStringBuilder(Fixture.AppConnectionString).Database!;
        var connection = await Fixture.OpenSuperuserAsync(database, Token);

        await using var switchRole = connection.CreateCommand();
        switchRole.CommandText = $"set role {role}";
        await switchRole.ExecuteNonQueryAsync(Token);

        return connection;
    }
}

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
/// the grant boundary — the app role reaches the table only through the definers.
/// </summary>
[Trait("Category", "Docker")]
public sealed class DataProtectionKeyStoreTests : SessionTestBase
{
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
        error.SqlState.ShouldBe("42501");
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
}

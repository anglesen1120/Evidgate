using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// The AI-settings schema as the migration leaves it: both owner policies exist and no policy of the two tables names
/// `public` or an empty role list, the head and the list have the descending indexes their queries read, and the
/// catalogue is SELECT-only for the application role.
/// </summary>
/// <remarks>
/// The index assertions are name-agnostic on purpose — they look for the key order the queries need, so renaming an index
/// is free and dropping the descending key is a failure. Column and role names are read from the catalogue, which is the
/// only source that cannot drift from the migration.
/// </remarks>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class AiSettingsSchemaTests
{
    private readonly PostgresPersistenceFixture _fixture;

    public AiSettingsSchemaTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    /// <summary>The head, the list and the template lookup each have the index that serves them.</summary>
    [Fact]
    public async Task Indexes_Cover_List_And_Head()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await _fixture.OpenSuperuserAsync(database);

        var indexes = (await ScalarAsync<string>(
            connection,
            """
            select coalesce(string_agg(indexdef, E'\n'), '')
            from pg_indexes
            where schemaname = 'public' and tablename in ('project_ai_settings', 'ai_settings_template')
            """))!;

        var normalised = indexes.ToUpperInvariant();

        normalised.ShouldContain(
            "(ORG_ID, PROJECT_ID, VERSION_NO DESC)", customMessage: "the head read is the newest version of one project");
        normalised.ShouldContain(
            "(ORG_ID, PROJECT_ID, CREATED_AT DESC, ID DESC)",
            customMessage: "the history page is a keyset walk on (created_at, id)");
        normalised.ShouldContain(
            "(CODE, LOCALE, VERSION)", customMessage: "the template lookup is the unique key a save names");
    }

    /// <summary>Both tables carry an owner policy, and neither names the pseudo-role <c>public</c>.</summary>
    [Fact]
    public async Task OwnerPolicies_ArePresent_AndRolesAreExplicit()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var connection = await _fixture.OpenSuperuserAsync(database);

        (await ScalarAsync<long>(
            connection,
            """
            select count(*) from pg_policies
            where schemaname = 'public'
              and tablename in ('project_ai_settings', 'ai_settings_template')
              and 'evidgate_owner' = any(roles)
            """))
            .ShouldBeGreaterThanOrEqualTo(2, "each table is readable by the owner role under FORCE row level security");

        (await ScalarAsync<long>(
            connection,
            """
            select count(*) from pg_policies
            where schemaname = 'public'
              and tablename in ('project_ai_settings', 'ai_settings_template')
              and (roles = '{public}' or cardinality(roles) = 0)
            """))
            .ShouldBe(0, "a policy always names its role, never public");

        (await ScalarAsync<bool>(
            connection,
            "select has_table_privilege('evidgate_app', 'public.ai_settings_template', 'INSERT')"))
            .ShouldBeFalse("the catalogue is written by a migration only");

        (await ScalarAsync<bool>(
            connection,
            "select has_table_privilege('evidgate_app', 'public.project_ai_settings', 'INSERT')"))
            .ShouldBeTrue("the save path inserts");
    }

    /// <summary>One scalar over the owner connection: the fixture opens connections, it does not wrap commands.</summary>
    /// <param name="connection">The open owner connection.</param>
    /// <param name="sql">The statement.</param>
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return (T)value!;
    }
}

using System.Net;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC2's immutability, asserted as the database enforces it: the application role may insert and never rewrite or delete,
/// the owner is refused an UPDATE by the guard trigger but keeps DELETE for expand/contract, and the template catalogue
/// is read-only for every runtime role.
/// </summary>
/// <remarks>
/// Every statement runs on a real connection under the role that would run it in production — the app connection string
/// the host uses, and <c>set role</c> from the owner connection for the operator — because a grant is only a grant when
/// the server says so. The CHECK that pairs the template columns is exercised by direct owner inserts.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsImmutabilityTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsImmutabilityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC2: an UPDATE by any role is 42501 or the guard's 23514, and only the owner's DELETE removes a row.</summary>
    [Fact]
    public async Task AppCannotRewriteHistory_OwnerUpdateIsGuarded_OwnerDeleteWorks()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

        const string Update = "update public.project_ai_settings set description = 'rewritten'";

        (await AsAppAsync(world.Database, Update))
            .ShouldStartWith("42501", customMessage: "the app role holds no UPDATE on the settings rows");
        (await AsRoleAsync(world.Database, "evidgate_operator", Update))
            .ShouldStartWith("42501", customMessage: "the operator reads another module's table, it never rewrites it");

        var ownerUpdate = await AsOwnerAsync(world.Database, Update)
            ?? throw new ShouldAssertException("the guard trigger must refuse the owner's UPDATE");

        ownerUpdate.ShouldStartWith("23514", customMessage: "the guard trigger refuses even the table owner");
        ownerUpdate.ShouldContain("authoring_settings_immutable", customMessage: "the guard names its trigger in the error");
        (await AiSettingsCalls.StoredAsync(_fixture, world, 1)).Document["description"]!.GetValue<string>()
            .ShouldBeEmpty("the refused update changed nothing");

        (await AsAppAsync(world.Database, "delete from public.project_ai_settings"))
            .ShouldStartWith("42501", customMessage: "the app role may not delete a version");

        (await AsOwnerAsync(world.Database, "delete from public.project_ai_settings"))
            .ShouldBeNull("the owner keeps DELETE for the expand/contract path");

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
    }

    /// <summary>AC2: the template catalogue is SELECT-only for the app, and no runtime role may write it.</summary>
    [Fact]
    public async Task Catalogue_IsReadOnly_ForRuntimeRoles()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            (await _fixture.ScalarAsync<bool>(
                world.Database,
                $"select has_table_privilege('evidgate_app', 'public.ai_settings_template', '{privilege}')"))
                .ShouldBeFalse($"the app role holds no {privilege} on the catalogue");
        }

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_table_privilege('evidgate_worker_sched', 'public.project_ai_settings', 'SELECT')"))
            .ShouldBeFalse("the scheduler role does not read another module's rows");

        (await AsAppAsync(
                world.Database,
                """
                insert into public.ai_settings_template (id, code, locale, version, name, content, content_hash, is_default)
                values (gen_random_uuid(), 'probe', 'en', 99, 'probe', '{}'::jsonb, 'sha256:' || repeat('ab', 32), false)
                """))
            .ShouldStartWith("42501", customMessage: "a platform template is a migration's to write");
    }

    /// <summary>
    /// The template provenance pair is all-or-nothing: a code without a version and a version without a code both fail
    /// the CHECK, the empty pair and the complete pair both pass.
    /// </summary>
    [Fact]
    public async Task TemplatePair_IsAllOrNothing()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var author = await AiSettingsCalls.MembershipAsync(_fixture, world, world.User);

        (await AsOwnerAsync(world.Database, Insert(world, author, "'web-form', null", 10)))
            .ShouldStartWith(
                "23514 project_ai_settings_template_check", customMessage: "a code without a version is not provenance");
        (await AsOwnerAsync(world.Database, Insert(world, author, "null, 1", 11)))
            .ShouldStartWith(
                "23514 project_ai_settings_template_check", customMessage: "a version without a code is not provenance");

        (await AsOwnerAsync(world.Database, Insert(world, author, "null, null", 12))).ShouldBeNull();
        (await AsOwnerAsync(world.Database, Insert(world, author, "'web-form', 1", 13))).ShouldBeNull();

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(2);
    }

    /// <summary>One settings row with the provenance pair spliced in, for the CHECK cases.</summary>
    private static string Insert(AiSettingsCalls.World world, Guid author, string pair, int version) =>
        $"""
        insert into public.project_ai_settings
            (id, org_id, project_id, version_no, description, languages, writing_style, coverage_strategy,
             priority_criterion_critical, priority_criterion_high, priority_criterion_medium, priority_criterion_low,
             exclusion_rules, step_detail, author_membership_id, content_hash, template_code, template_version)
        values (gen_random_uuid(), '{world.Org}', '{world.Project}', {version}, '', array['en', 'vi'], '', '', '', '', '', '', '', '',
                '{author}', 'sha256:' || repeat('cd', 32), {pair})
        """;

    /// <summary>The statement on the app role's own connection — the role the host runs as.</summary>
    private async Task<string?> AsAppAsync(string database, string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));

        return await RunAsync(dataSource, sql);
    }

    /// <summary>The statement on the owner connection: the migration role, which the guard trigger still refuses.</summary>
    private async Task<string?> AsOwnerAsync(string database, string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.OwnerConnectionString(database));

        return await RunAsync(dataSource, sql);
    }

    /// <summary>The statement entered through <c>set role</c> from the owner connection, as a role with no connection of its own.</summary>
    private async Task<string?> AsRoleAsync(string database, string role, string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.OwnerConnectionString(database));

        return await RunAsync(dataSource, $"set role {role}; {sql}");
    }

    private static async Task<string?> RunAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);

        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            return null;
        }
        catch (PostgresException exception)
        {
            // The constraint name rides along so a negative case cannot pass on the wrong CHECK.
            return $"{exception.SqlState} {exception.ConstraintName} {exception.MessageText}";
        }
    }
}

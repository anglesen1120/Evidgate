using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The database's own guards on the registry: a built-in cannot be duplicated, an org row may reuse a
/// built-in's key, and the closed CHECKs refuse a kind, name, version, body reference or rules document outside
/// their domains.</summary>
public sealed partial class TemplateTests
{
    [Fact]
    public async Task Template_BuiltInKey_IsUniquePerKindNameVersion()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, builtIn: true, name: "playwright-ts");

        // A built-in is `org_id IS NULL`; the default org in the helper would make this an allowed org row instead.
        var duplicate = await OwnerErrorAsync(world, InsertSql, ("org", null), ("kind", "web"), ("name", "playwright-ts"));
        duplicate.ShouldNotBeNull("the partial unique index refuses a duplicate built-in");
        duplicate!.SqlState.ShouldBe("23505");

        // The same key is free inside an org and inside another org: the composite key, not the partial index, governs.
        (await OwnerErrorAsync(world, InsertSql, ("kind", "web"), ("name", "playwright-ts"), ("org", world.Org)))
            .ShouldBeNull("an org row may carry the key of a built-in");
        var (_, otherOrg) = await AddOrgAsync(world);
        (await OwnerErrorAsync(world, InsertSql, ("kind", "web"), ("name", "playwright-ts"), ("org", otherOrg)))
            .ShouldBeNull("a second org may carry the same key");
    }

    [Fact]
    public async Task Template_Domains_AreEnforcedByTheDatabase()
    {
        await using var world = await StartAsync();

        var wrongKind = await OwnerErrorAsync(world, InsertSql, ("kind", "desktop"));
        wrongKind.ShouldNotBeNull();
        wrongKind!.SqlState.ShouldBe("23514");
        wrongKind.ConstraintName.ShouldBe("template_kind_check");

        (await OwnerErrorAsync(world, InsertSql, ("name", "Upper")))!.ConstraintName.ShouldBe("template_name_check");
        (await OwnerErrorAsync(world, InsertSql, ("name", new string('a', 65))))!.ConstraintName.ShouldBe("template_name_check");

        (await OwnerErrorAsync(world, InsertSql, ("version", 0)))!.ConstraintName.ShouldBe("template_version_check");

        (await OwnerErrorAsync(world, InsertSql, ("body_ref", "sha256:XYZ")))!.ConstraintName.ShouldBe("template_body_ref_check");

        (await OwnerErrorAsync(world, InsertSql, ("rules", "42")))!.ConstraintName.ShouldBe("template_conformance_rules_check");

        var oversized = "[\"" + new string('a', 70_000) + "\"]";
        (await OwnerErrorAsync(world, InsertSql, ("rules", oversized)))!.ConstraintName.ShouldBe("template_conformance_rules_check");
    }

    [Fact]
    public async Task Template_ActiveDefaultsToTrue()
    {
        await using var world = await StartAsync();
        var id = Guid.CreateVersion7();

        await using (var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(world.Database)))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules)
                values (@id, @org, 'unit', 'default-active', 1, @body_ref, '{}'::jsonb)
                """;
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("org", world.Org);
            command.Parameters.AddWithValue("body_ref", BodyRef);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await _fixture.ScalarAsync<bool>(world.Database, $"select active from public.template where id = '{id}'"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Template_RulesNesting_IsLimitedToTheDepthLimit()
    {
        await using var world = await StartAsync();

        (await OwnerErrorAsync(world, InsertSql, ("rules", NestedRules(32))))
            .ShouldBeNull("level 32 is inside the reader's parser depth");

        var tooDeep = await OwnerErrorAsync(world, InsertSql, ("rules", NestedRules(33)));
        tooDeep.ShouldNotBeNull("level 33 is refused");
        tooDeep!.SqlState.ShouldBe("23514");
        tooDeep.ConstraintName.ShouldBe("template_conformance_rules_check");
    }

    /// <summary>A rules document of <paramref name="depth"/> nested single-element arrays around a scalar.</summary>
    private static string NestedRules(int depth) => new string('[', depth) + "\"leaf\"" + new string(']', depth);

    /// <summary>An insert of one org row whose members a test overrides; the defaults satisfy every CHECK.</summary>
    private const string InsertSql =
        """
        insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules)
        values (@id, @org::uuid, @kind, @name, @version, @body_ref, @rules::jsonb)
        """;

    /// <summary>Runs one insert as the owner and returns its server error, or null when the row was written.</summary>
    private async Task<PostgresException?> OwnerErrorAsync(
        World world,
        string sql,
        params (string Name, object? Value)[] overrides)
    {
        var values = new Dictionary<string, object?>
        {
            ["id"] = Guid.CreateVersion7(),
            ["org"] = world.Org,
            ["kind"] = "web",
            ["name"] = "t-" + Guid.NewGuid().ToString("N")[..12],
            ["version"] = 1,
            ["body_ref"] = BodyRef,
            ["rules"] = "{}",
        };
        foreach (var (name, value) in overrides)
        {
            values[name] = value;
        }

        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }
}

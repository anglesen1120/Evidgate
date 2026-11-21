using System.Text;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The database's own guards on the registry: a built-in cannot be duplicated, an org row cannot reuse a
/// built-in's key (and the reverse), and the closed CHECKs refuse a kind, name, version, body reference or rules document outside
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
    }

    [Fact]
    public async Task Template_OrgRowWithABuiltInKey_IsRefused()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, builtIn: true, name: "playwright-ts");

        var reuse = await OwnerErrorAsync(world, InsertSql, ("kind", "web"), ("name", "playwright-ts"), ("org", world.Org));
        reuse.ShouldNotBeNull("an org row may not carry the key of a built-in");
        reuse!.SqlState.ShouldBe("23505");
        reuse.ConstraintName.ShouldBe("template_key_reuse");

        // Each key column is part of the key: a new version, name or kind is a new key.
        (await OwnerErrorAsync(world, InsertSql, ("name", "playwright-ts"), ("version", 2), ("org", world.Org)))
            .ShouldBeNull("a new version is a new key");
        (await OwnerErrorAsync(world, InsertSql, ("name", "playwright-ts-2"), ("org", world.Org)))
            .ShouldBeNull("a new name is a new key");
        (await OwnerErrorAsync(world, InsertSql, ("kind", "api"), ("name", "playwright-ts"), ("org", world.Org)))
            .ShouldBeNull("a new kind is a new key");
    }

    [Fact]
    public async Task Template_OrgRowsMayShareAKeyAcrossOrgs()
    {
        await using var world = await StartAsync();
        var (_, otherOrg) = await AddOrgAsync(world);

        (await OwnerErrorAsync(world, InsertSql, ("name", "shared-key"), ("org", world.Org))).ShouldBeNull();
        (await OwnerErrorAsync(world, InsertSql, ("name", "shared-key"), ("org", otherOrg)))
            .ShouldBeNull("the composite key, not the built-in rule, governs two orgs");
    }

    [Fact]
    public async Task Template_BuiltInOverAnOrgKey_IsRefused()
    {
        await using var world = await StartAsync();
        (await OwnerErrorAsync(world, InsertSql, ("name", "org-first"), ("org", world.Org))).ShouldBeNull();

        var reuse = await OwnerErrorAsync(world, InsertSql, ("name", "org-first"), ("org", null));
        reuse.ShouldNotBeNull("a built-in may not take the key an org row already holds");
        reuse!.SqlState.ShouldBe("23505");
        reuse.ConstraintName.ShouldBe("template_key_reuse");

        (await OwnerErrorAsync(world, InsertSql, ("name", "org-first-free"), ("org", null)))
            .ShouldBeNull("a built-in with an unused key is accepted");
    }

    [Fact]
    public async Task Template_UpdatingAKeyColumnOntoABuiltInKey_IsRefused()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, builtIn: true, name: "playwright-ts");
        var orgRow = await SeedAsync(world, org: world.Org, name: "mine");

        const string Rename = "update public.template set name = @name where id = @id";

        (await OwnerErrorAsync(world, Rename, ("id", orgRow), ("name", "mine-renamed")))
            .ShouldBeNull("a rename onto an unused key is accepted");

        var error = await OwnerErrorAsync(world, Rename, ("id", orgRow), ("name", "playwright-ts"));
        error.ShouldNotBeNull("a rename onto a built-in key is the same reuse as an insert");
        error!.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("template_key_reuse");
        (await _fixture.ScalarAsync<string>(world.Database, $"select name from public.template where id = '{orgRow}'"))
            .ShouldBe("mine-renamed", "the refused update leaves the row as it was");
    }

    [Fact]
    public async Task Template_ChangingAKeyColumnOrTheOwner_IsCheckedAgainstTheOtherSide()
    {
        await using var world = await StartAsync();
        await SeedAsync(world, builtIn: true, name: "taken");
        var sole = await SeedAsync(world, org: world.Org, name: "promote-me");
        var otherKind = await SeedAsync(world, org: world.Org, name: "taken", kind: "api");

        (await OwnerErrorAsync(world, "update public.template set org_id = null where id = @id", ("id", sole)))
            .ShouldBeNull("promoting the only row for a key leaves no second row with it");

        var kindOnly = await OwnerErrorAsync(world, "update public.template set kind = 'web' where id = @id", ("id", otherKind));
        kindOnly.ShouldNotBeNull("a kind change onto a built-in key is refused");
        kindOnly!.ConstraintName.ShouldBe("template_key_reuse");

        var versionOnly = await OwnerErrorAsync(
            world, "update public.template set version = 2 where id = @id", ("id", otherKind));
        versionOnly.ShouldBeNull("a version change onto an unused key is accepted");
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
            .ShouldBeNull("32 nested containers around a scalar are inside the limit");

        var tooDeep = await OwnerErrorAsync(world, InsertSql, ("rules", NestedRules(33)));
        tooDeep.ShouldNotBeNull("33 nested containers around a scalar are refused");
        tooDeep!.SqlState.ShouldBe("23514");
        tooDeep.ConstraintName.ShouldBe("template_conformance_rules_check");
    }

    [Theory]
    [InlineData("arrays", 32, true)]
    [InlineData("arrays", 33, false)]
    [InlineData("objects", 32, true)]
    [InlineData("objects", 33, false)]
    [InlineData("mixed", 32, true)]
    [InlineData("mixed", 33, false)]
    public async Task Template_RulesNesting_CountsEmptyContainersToo(string shape, int containers, bool accepted)
    {
        await using var world = await StartAsync();

        var error = await OwnerErrorAsync(world, InsertSql, ("rules", EmptyNesting(shape, containers)));

        if (accepted)
        {
            error.ShouldBeNull($"{containers} nested empty {shape} are inside the limit");
            return;
        }

        error.ShouldNotBeNull($"{containers} nested empty {shape} are refused");
        error!.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("template_conformance_rules_check");
    }

    /// <summary>Nested containers with no scalar inside; an object level wraps the next level under key "k".</summary>
    private static string EmptyNesting(string shape, int containers)
    {
        var open = new StringBuilder();
        var close = new StringBuilder();
        for (var level = 0; level < containers; level++)
        {
            var asObject = shape == "objects" || (shape == "mixed" && level % 2 == 0);
            var last = level == containers - 1;
            open.Append(asObject ? (last ? "{" : "{\"k\":") : "[");
            close.Insert(0, asObject ? "}" : "]");
        }

        return open.ToString() + close;
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

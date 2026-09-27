using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.1.2-T1.1 / 02.1.2 §4: the shape of <c>membership</c> and <c>membership_role</c>
/// on the migrated database — the tenant key, the composite tenant foreign key, the
/// uniques AC1 and the invite lookup need, ENABLE + FORCE row level security with the
/// NULL-safe tenant policy, the 00 §7 columns and the shared version trigger.
/// </summary>
/// <remarks>
/// The catalogue read goes through <c>pg_catalog</c> rather than a hand-written list
/// of expectations, and the RLS half runs through a real tenant scope as
/// <c>evidgate_app</c> — the same role the API uses, never the owner, which the
/// permissive owner policy would let past every check (DECISIONS F5).
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class MembershipSchemaTests : CatalogueTestBase
{
    public MembershipSchemaTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Both tables are tenant tables in the full sense of 00 §5.4: <c>unique (org_id, id)</c>
    /// (so each can be a composite-FK target), ENABLE + FORCE RLS with a tenant policy and
    /// an owner policy, and the 00 §7 audit columns under the shared version trigger.
    /// </summary>
    [Fact]
    public async Task Tables_Carry_The_Tenant_Key_Rls_And_Audit_Columns()
    {
        foreach (var table in new[] { "membership", "membership_role" })
        {
            (await ScalarAsync<long>(
                """
                select count(*)
                  from pg_index i
                  join pg_class c on c.oid = i.indrelid
                 where c.relname = @table
                   and i.indisunique and i.indisvalid and i.indimmediate and i.indpred is null
                   and (select array_agg(a.attname::text order by a.attname::text)
                          from generate_series(0, i.indnkeyatts - 1) s(ord)
                          join pg_attribute a on a.attrelid = i.indrelid and a.attnum = i.indkey[s.ord])
                       = array['id', 'org_id']
                """,
                ("table", table))).ShouldBe(1, $"{table} declares unique (org_id, id) (TL T3)");

            var flags = await RowAsync(
                "select relrowsecurity, relforcerowsecurity from pg_class where relname = @table",
                ("table", table));

            flags[0].ShouldBe(true, $"{table}.relrowsecurity");
            flags[1].ShouldBe(true, $"{table}.relforcerowsecurity (DECISIONS F5)");

            (await ScalarAsync<long>(
                "select count(*) from pg_policies where schemaname = 'public' and tablename = @table and policyname = @policy",
                ("table", table),
                ("policy", $"{table}_app_tenant"))).ShouldBe(1, $"{table} carries its tenant policy (TL T2)");

            (await ScalarAsync<long>(
                "select count(*) from pg_policies where schemaname = 'public' and tablename = @table and policyname = @policy",
                ("table", table),
                ("policy", $"{table}_owner_backfill"))).ShouldBe(1, $"{table} carries the owner policy the seed/backfill needs");

            (await ScalarAsync<long>(
                """
                select count(*)
                  from pg_trigger t
                  join pg_class c on c.oid = t.tgrelid
                 where c.relname = @table
                   and t.tgname = @trigger
                   and not t.tgisinternal
                   and t.tgenabled <> 'D'
                   and pg_get_triggerdef(t.oid) ilike '%before update%'
                """,
                ("table", table),
                ("trigger", $"{table}_set_updated_at_and_version")))
                .ShouldBe(1, $"{table} carries the shared version trigger (00 §7)");

            foreach (var column in new[] { "created_at", "updated_at", "version" })
            {
                (await ScalarAsync<long>(
                    "select count(*) from information_schema.columns where table_schema = 'public' and table_name = @table and column_name = @column and is_nullable = 'NO'",
                    ("table", table),
                    ("column", column))).ShouldBe(1, $"{table}.{column} is a NOT NULL 00 §7 column");
            }
        }
    }

    /// <summary>
    /// AC1 and the invite lookup: one pending invite per (org, address) — the partial
    /// unique index, not a table constraint — one membership per (org, user) once a
    /// login binds it, and a platform-unique public prefix for
    /// <c>evidgate_resolve.invite_by_secret</c> (T75).
    /// </summary>
    [Fact]
    public async Task Invite_And_Membership_Uniques_Are_Declared()
    {
        (await IndexDefinitionAsync("membership_pending_email_key")).ShouldContain(
            "unique", Case.Insensitive);
        (await IndexDefinitionAsync("membership_pending_email_key")).ShouldContain(
            "status = 'pending'", Case.Insensitive, "only a PENDING invite blocks a second one (AC1)");

        (await IndexDefinitionAsync("membership_org_user_key")).ShouldContain(
            "user_id IS NOT NULL", Case.Insensitive, "one membership per user per org, pending rows excluded");

        (await IndexDefinitionAsync("membership_invite_secret_prefix_key")).ShouldContain(
            "unique", Case.Insensitive);

        (await IndexDefinitionAsync("membership_role_membership_role_key")).ShouldContain(
            "unique", Case.Insensitive, "a member holds a role once");

        (await IndexDefinitionAsync("membership_org_created_id_idx")).ShouldContain(
            "created_at", Case.Insensitive, "the 00 §9.1 keyset order of GET /v1/orgs/{o}/memberships");
    }

    /// <summary>
    /// The composite tenant foreign key AC7 rests on, and the recorded deviation of the
    /// plan's role key: <c>membership_role.role_id</c> carries NO foreign key at all —
    /// a composite key to <c>role (org_id, id)</c> could never match a built-in role
    /// (0010 seeds them with <c>org_id IS NULL</c>) and a single-column key is refused by
    /// 02.9.1-T2's composite-FK rule for any tenant table (migration 0011's header, 00 §15
    /// 2026-09-27).
    /// </summary>
    [Fact]
    public async Task Membership_Role_References_Membership_Compositely_And_Role_By_Value()
    {
        var foreignKeys = await ForeignKeysAsync("membership_role");

        foreignKeys.Count.ShouldBe(1, "the only declarative reference is the composite tenant one");

        var composite = foreignKeys[0];
        composite.Name.ShouldBe("membership_role_membership_fk");
        composite.Columns.ShouldBe(["org_id", "membership_id"]);
        composite.Principal.ShouldBe("public.membership");
        composite.PrincipalColumns.ShouldBe(["org_id", "id"]);

        (await ScalarAsync<long>(
            """
            select count(*)
              from pg_constraint con
              join pg_class c on c.oid = con.conrelid
              join pg_class rc on rc.oid = con.confrelid
             where con.contype = 'f' and c.relname = 'membership_role' and rc.relname = 'role'
            """)).ShouldBe(0, "role_id is a by-value reference, enforced by GrantRoleCommand (409 unknown_role)");
    }

    /// <summary>
    /// FR-02-10/AC7 through the real runtime role: inside a tenant scope the app role
    /// sees only its own org's memberships and role grants, and cannot write a row filed
    /// under another org (the RLS <c>WITH CHECK</c>).
    /// </summary>
    [Fact]
    public async Task Tenant_Scope_Hides_Another_Orgs_Memberships_And_Refuses_Its_Writes()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await SeedMembershipAsync(orgA, "a@example.com");
        await SeedMembershipAsync(orgB, "b@example.com");

        await using (var scope = await Fixture.BeginTenantScopeAsync(orgA, readOnlyTransaction: true, cancellationToken: Token))
        {
            (await scope.Db.ScalarAsync<long>("select count(*) from public.membership", cancellationToken: Token))
                .ShouldBe(1);
            (await scope.Db.ScalarAsync<long>(
                "select count(*) from public.membership where org_id = @org_id",
                new { org_id = orgB },
                Token)).ShouldBe(0, "org B's row is not reachable from org A's scope");
            (await scope.Db.ScalarAsync<long>("select count(*) from public.membership_role", cancellationToken: Token))
                .ShouldBe(1);
        }

        await using (var scope = await Fixture.BeginTenantScopeAsync(orgA, cancellationToken: Token))
        {
            var refusal = await Should.ThrowAsync<PostgresException>(async () =>
                await scope.Db.ExecuteAsync(
                    "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, 'pending')",
                    new { id = Guid.CreateVersion7(), org_id = orgB, email = "sneaky@example.com" },
                    Token));

            refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            refusal.MessageText.ShouldContain("row-level security policy");
        }
    }

    /// <summary>
    /// 00 §5.4.7 fail-closed: with no tenant setting the app role reads zero rows from
    /// both tables — never every row and never an error — because the policy compares
    /// against the NULL-safe helper.
    /// </summary>
    [Fact]
    public async Task Without_A_Tenant_Setting_Neither_Table_Returns_Rows()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedMembershipAsync(org, "a@example.com");

        await using var command = Fixture.AppDataSource.CreateCommand(
            "select (select count(*) from public.membership), (select count(*) from public.membership_role)");

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        reader.GetInt64(0).ShouldBe(0, "no tenant setting → zero memberships");
        reader.GetInt64(1).ShouldBe(0, "no tenant setting → zero role grants");
    }

    /// <summary>Seeds one pending membership with the built-in <c>tenant_admin</c> role, as the owner (arranging rows only).</summary>
    private async Task SeedMembershipAsync(Guid orgId, string email)
    {
        var roleId = await ScalarAsync<Guid>(
            "select id from public.role where org_id is null and code = 'tenant_admin'");
        var membershipId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, 'pending')",
            [("id", membershipId), ("org_id", orgId), ("email", email)],
            Token);

        await Fixture.SeedAsync(
            "insert into public.membership_role (id, org_id, membership_id, role_id) values (@id, @org_id, @membership_id, @role_id)",
            [("id", Guid.CreateVersion7()), ("org_id", orgId), ("membership_id", membershipId), ("role_id", roleId)],
            Token);
    }

    /// <summary>One scalar on the owner connection (catalogue reads and row arrangement).</summary>
    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Token);
        return (T)result!;
    }

    /// <summary>One row of scalars on the owner connection.</summary>
    private async Task<object?[]> RowAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var values = new object?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = await reader.IsDBNullAsync(index, Token) ? null : reader.GetValue(index);
        }

        return values;
    }

    /// <summary>The index's own definition, as PostgreSQL reports it (<c>pg_indexes.indexname</c> is unqualified).</summary>
    private async Task<string> IndexDefinitionAsync(string indexName) =>
        await ScalarAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = @index",
            ("index", indexName));

    /// <summary>The foreign keys of one table, with their positionally paired column lists.</summary>
    private async Task<IReadOnlyList<ForeignKeyShape>> ForeignKeysAsync(string table)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select con.conname,
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(con.conkey) with ordinality k(attnum, ord)
                      join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum),
                   rn.nspname || '.' || rc.relname,
                   (select array_agg(a.attname::text order by k.ord)
                      from unnest(con.confkey) with ordinality k(attnum, ord)
                      join pg_attribute a on a.attrelid = con.confrelid and a.attnum = k.attnum)
              from pg_constraint con
              join pg_class c on c.oid = con.conrelid
              join pg_class rc on rc.oid = con.confrelid
              join pg_namespace rn on rn.oid = rc.relnamespace
             where con.contype = 'f' and c.relname = @table
             order by con.conname
            """;
        command.Parameters.AddWithValue("table", table);

        var keys = new List<ForeignKeyShape>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            keys.Add(new ForeignKeyShape(
                reader.GetString(0),
                await reader.GetFieldValueAsync<string[]>(1, Token),
                reader.GetString(2),
                await reader.GetFieldValueAsync<string[]>(3, Token)));
        }

        return keys;
    }

    /// <summary>One foreign key as the suite reads it.</summary>
    private sealed record ForeignKeyShape(string Name, string[] Columns, string Principal, string[] PrincipalColumns);
}

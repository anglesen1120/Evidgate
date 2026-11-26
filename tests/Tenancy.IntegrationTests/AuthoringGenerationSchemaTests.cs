using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Migration 0133 through a real PostgreSQL: the intake and extraction-slot tables, their composite tenant
/// keys, FORCE RLS, column-scoped grants, the guard trigger (every immutable change and every illegal
/// transition answers 23514, DELETE belongs to the owner alone), and the two new artifact kinds on the
/// kind checks of artifact, artifact_kind_policy and redaction_rule.
/// <para>
/// The app role holds column-scoped grants, so a write to a submitted column is refused with 42501 before the
/// trigger runs; the trigger itself is therefore driven as the owner, which the grants do not limit.
/// </para>
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringGenerationSchemaTests(PostgresTenancyFixture fixture)
{
    private const string Intake = "authoring_intake";
    private const string Slot = "authoring_intake_slot";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private const string InsertIntake = """
        insert into public.authoring_intake
            (id, org_id, project_id, feature_node_id, kind, artifact_id, input_text, media_type, declared_bytes, caps, created_by)
        values (@id, @org, @project, @node, @kind, @artifact, @text, @media, 5, '{}', @member)
        """;

    private const string InsertIntakeWithState = """
        insert into public.authoring_intake
            (id, org_id, project_id, feature_node_id, kind, input_text, media_type, declared_bytes, caps, state)
        values (@id, @org, @project, @node, 'text', 'hello', 'text/plain', 5, '{}', 'extracting')
        """;

    private const string InsertSlot = """
        insert into public.authoring_intake_slot (id, org_id, slot, intake_id, lease_id, expires_at)
        values (gen_random_uuid(), @org, @slot, @intake, gen_random_uuid(), now() + interval '1 minute')
        """;

    private const string InsertArtifact = """
        insert into public.artifact
            (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity)
        values (@id, @org, @project, @run, repeat('a', 64), 1, @kind, @key, @media, 'sha256:' || repeat('b', 64), 'sensitive')
        """;

    private const string HashSql = "extracted_text = 'body', extracted_text_hash = 'sha256:' || repeat('c', 64)";

    // ------------------------------------------------------------------ shape: RLS, grants, indexes, kinds

    /// <summary>Both tables: FORCE RLS, an app and an owner policy each, and no privilege beyond the declared ones.</summary>
    [Fact]
    public async Task Shape_ForcesRlsAndGrantsOnlyTheDeclaredColumns()
    {
        var database = await fixture.SharedDatabaseAsync();
        foreach (var table in new[] { Intake, Slot })
        {
            (await fixture.RowSecurityAsync(database, table)).ShouldBe((true, true), table);
            (await fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "TRUNCATE")).ShouldBeFalse(table);
            (await fixture.HasTablePrivilegeAsync(database, "evidgate_operator", table, "SELECT")).ShouldBeFalse(table);
            (await fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", table, "SELECT")).ShouldBeFalse(table);
        }

        (await fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_policies where schemaname = 'public' and tablename in ('authoring_intake', 'authoring_intake_slot') "
            + "and roles::text[] <@ array['evidgate_app', 'evidgate_owner']")).ShouldBe(4L);

        (await fixture.HasTablePrivilegeAsync(database, "evidgate_app", Intake, "DELETE")).ShouldBeFalse();
        (await fixture.HasTablePrivilegeAsync(database, "evidgate_app", Slot, "DELETE")).ShouldBeTrue();
        (await fixture.IndexesAsync(database, Intake)).ShouldContain(
            index => index.Contains("(org_id, project_id, created_at DESC, id DESC)", StringComparison.Ordinal));
        (await fixture.IndexesAsync(database, Intake)).ShouldContain(
            index => index.Contains("authoring_intake_pending_created_idx", StringComparison.Ordinal)
                && index.Contains("(org_id, created_at, id)", StringComparison.Ordinal)
                && index.Contains("received", StringComparison.Ordinal)
                && index.Contains("extracting", StringComparison.Ordinal)
                && index.Contains("WHERE", StringComparison.Ordinal));
        foreach (var column in new[] { "id", "org_id", "slot", "intake_id", "lease_id", "expires_at" })
        {
            (await fixture.ScalarAsync<bool>(database, $"select has_column_privilege('evidgate_app', 'public.{Slot}', '{column}', 'UPDATE')"))
                .ShouldBe(column == "expires_at", $"slot update {column}");
        }

        foreach (var (column, update, insert) in new[]
        {
            ("state", true, false), ("extracted_text", true, false), ("failure_code", true, false), ("caps", false, true),
            ("kind", false, true), ("input_text", false, true), ("artifact_id", false, true), ("created_at", false, false),
        })
        {
            (await fixture.ScalarAsync<bool>(database, $"select has_column_privilege('evidgate_app', 'public.{Intake}', '{column}', 'UPDATE')"))
                .ShouldBe(update, $"update {column}");
            (await fixture.ScalarAsync<bool>(database, $"select has_column_privilege('evidgate_app', 'public.{Intake}', '{column}', 'INSERT')"))
                .ShouldBe(insert, $"insert {column}");
        }
    }

    /// <summary>The two new kinds pass the three kind checks and an unknown one fails each.</summary>
    [Fact]
    public async Task NewKinds_PassTheKindChecks()
    {
        var w = await SeedAsync();
        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(w.Database));

        foreach (var kind in new[] { "requirement_document", "requirement_image" })
        {
            (await owner.ExecuteAsync(
                "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
                + "values (gen_random_uuid(), @org, @project, @kind, 1048576, array['image/png'], 'sensitive')",
                ("org", w.Org), ("project", w.Project), ("kind", kind))).ShouldBe(1);
            (await owner.ExecuteAsync(
                "insert into public.redaction_rule (id, org_id, project_id, kind, rule_type, name_pattern) "
                + "values (gen_random_uuid(), @org, @project, @kind, 'header', 'x-probe')",
                ("org", w.Org), ("project", w.Project), ("kind", kind))).ShouldBe(1);
            (await owner.ExecuteAsync(InsertArtifact, ArtifactArgs(w, Guid.CreateVersion7(), kind, "image/png"))).ShouldBe(1);
        }

        await RefusesAsync(owner, InsertArtifact, "23514", ArtifactArgs(w, Guid.CreateVersion7(), "requirement_pdf", "image/png"));
        await RefusesAsync(
            owner,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + "values (gen_random_uuid(), @org, @project, 'requirement_pdf', 1, array['image/png'], 'sensitive')",
            "23514",
            ("org", w.Org), ("project", w.Project));
        await RefusesAsync(
            owner,
            "insert into public.redaction_rule (id, org_id, project_id, kind, rule_type, name_pattern) "
            + "values (gen_random_uuid(), @org, @project, 'requirement_pdf', 'header', 'x-probe')",
            "23514",
            ("org", w.Org), ("project", w.Project));
    }

    // ------------------------------------------------------------------ rows: shape, tenant keys, isolation

    /// <summary>One valid row per kind, the shape checks, the composite FKs and the tenant isolation of a committed row.</summary>
    [Fact]
    public async Task Rows_AreIsolatedComposedAndShapeChecked()
    {
        var w = await SeedAsync();
        var imageArtifact = await NewArtifactAsync(w, "requirement_image", "image/png");
        var pdfArtifact = await NewArtifactAsync(w, "requirement_document", "application/pdf");
        var text = Guid.CreateVersion7();

        await using (var scope = await AppAsync(w))
        {
            (await scope.ExecuteAsync(InsertIntake, Row(w, "text", id: text))).ShouldBe(1);
            (await scope.ExecuteAsync(InsertIntake, Row(w, "image", imageArtifact))).ShouldBe(1);
            (await scope.ExecuteAsync(InsertIntake, Row(w, "pdf", pdfArtifact))).ShouldBe(1);
            (await scope.ScalarAsync<string>("select state from public.authoring_intake where id = @id", ("id", text))).ShouldBe("received");
            (await scope.ScalarAsync<int>("select version from public.authoring_intake where id = @id", ("id", text))).ShouldBe(1);
            await scope.CommitAsync();
        }

        foreach (var org in new Guid?[] { w.Org, null, Guid.CreateVersion7() })
        {
            await using var reader = await TenantScope.BeginAsync(fixture.AppConnectionString(w.Database), org);
            (await reader.ScalarAsync<long>("select count(*) from public.authoring_intake where id = @id", ("id", text)))
                .ShouldBe(org == w.Org ? 1L : 0L);
        }

        await using var check = await AppAsync(w);
        var other = await NewArtifactAsync(w, "requirement_document", "application/pdf");
        await RefusesAsync(check, InsertIntake, "23503", With(Row(w, "text"), "project", w.Sibling));
        await RefusesAsync(check, InsertIntake, "23503", With(Row(w, "pdf", other), "project", w.Sibling));
        await RefusesAsync(check, InsertIntake, "23503", With(Row(w, "text"), "node", Guid.CreateVersion7()));
        await RefusesAsync(check, InsertIntake, "23503", With(Row(w, "pdf", other), "artifact", Guid.CreateVersion7()));
        await RefusesAsync(check, InsertIntake, "23503", With(Row(w, "text"), "member", Guid.CreateVersion7()));
        await RefusesAsync(check, InsertIntake, "23505", Row(w, "pdf", pdfArtifact));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "pdf", other), "node", DBNull.Value));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "image", other), "node", w.Node));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "text"), "artifact", other));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "pdf", other), "text", "inline"));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "pdf", other), "media", "image/png"));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "image", other), "media", "image/gif"));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "text"), "text", string.Empty));
        await RefusesAsync(check, InsertIntake, "23514", With(Row(w, "text"), "text", new string('x', 1_048_577)));
        await RefusesAsync(check, InsertIntake.Replace(", 5,", ", 0,", StringComparison.Ordinal), "23514", Row(w, "text"));
        await RefusesAsync(check, InsertIntake.Replace("'{}'", "'[]'", StringComparison.Ordinal), "23514", Row(w, "text"));
        await RefusesAsync(check, InsertIntakeWithState, "42501", ("id", Guid.CreateVersion7()), ("org", w.Org), ("project", w.Project), ("node", w.Node));
    }

    // ------------------------------------------------------------------ guard: transitions

    /// <summary>
    /// Text and image go received to ready directly, pdf and docx must pass extracting, nothing returns to received, and a
    /// finished row is frozen while a write that changes nothing still passes.
    /// </summary>
    [Theory]
    [InlineData("text")]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("image")]
    public async Task Transitions_FollowTheTableAndSealFinishedRows(string kind)
    {
        var w = await SeedAsync();
        var artifact = kind == "text" ? (Guid?)null : await NewArtifactAsync(w, kind == "image" ? "requirement_image" : "requirement_document", MediaOf(kind));
        var id = Guid.CreateVersion7();
        await using var scope = await AppAsync(w);
        (await scope.ExecuteAsync(InsertIntake, Row(w, kind, artifact, id))).ShouldBe(1);

        var ready = kind == "image" ? "state = 'ready'" : $"state = 'ready', {HashSql}";
        if (kind is "pdf" or "docx")
        {
            await RefusesAsync(scope, Update(ready), "23514", ("id", id));
            (await scope.ExecuteAsync(Update("state = 'extracting'"), ("id", id))).ShouldBe(1);
            (await scope.ExecuteAsync(Update("state = 'extracting'"), ("id", id))).ShouldBe(1);
            await RefusesAsync(scope, Update("state = 'received'"), "23514", ("id", id));
            await RefusesAsync(scope, Update("state = 'ready'"), "23514", ("id", id));
            await RefusesAsync(scope, Update("state = 'rejected'"), "23514", ("id", id));
            await RefusesAsync(scope, Update("state = 'rejected', failure_code = 'bogus'"), "23514", ("id", id));
        }
        else if (kind == "text")
        {
            await RefusesAsync(scope, Update("state = 'ready'"), "23514", ("id", id));
        }
        else
        {
            await RefusesAsync(scope, Update("state = 'ready', extracted_text = 'x', extracted_text_hash = 'sha256:' || repeat('c', 64)"), "23514", ("id", id));
        }

        (await scope.ExecuteAsync(Update(ready), ("id", id))).ShouldBe(1);
        (await scope.ExecuteAsync(Update("state = 'ready'"), ("id", id))).ShouldBe(1);
        await RefusesAsync(scope, Update("state = 'rejected', failure_code = 'extraction_failed'"), "23514", ("id", id));
        await RefusesAsync(scope, Update("state = 'extracting'"), "23514", ("id", id));
    }

    /// <summary>A rejection names one of the ten failure codes, keeps no text, and is final; it may follow received or extracting.</summary>
    [Fact]
    public async Task Rejection_NamesAKnownCodeAndIsFinal()
    {
        var w = await SeedAsync();
        await using var scope = await AppAsync(w);
        var first = await InsertDocumentAsync(scope, w, "pdf");
        var second = await InsertDocumentAsync(scope, w, "docx");

        (await scope.ExecuteAsync(Update("state = 'rejected', failure_code = 'macro_present'"), ("id", first))).ShouldBe(1);
        (await scope.ExecuteAsync(Update("state = 'extracting'"), ("id", second))).ShouldBe(1);
        await RefusesAsync(scope, Update("state = 'rejected', failure_code = 'bogus'"), "23514", ("id", second));
        await RefusesAsync(scope, Update($"state = 'rejected', failure_code = 'no_extractable_text', {HashSql}"), "23514", ("id", second));
        await RefusesAsync(scope, Update("failure_code = 'extraction_failed'"), "23514", ("id", second));
        (await scope.ExecuteAsync(Update("state = 'rejected', failure_code = 'extraction_timeout'"), ("id", second))).ShouldBe(1);

        foreach (var set in new[] { "state = 'ready'", "state = 'extracting'", "state = 'received'", "failure_code = 'extraction_failed'" })
        {
            await RefusesAsync(scope, Update(set), "23514", ("id", first));
        }
    }

    // ------------------------------------------------------------------ guard: immutable columns, delete

    /// <summary>
    /// The owner is not limited by the column grants, so it reaches the trigger: every submitted column, the tenant
    /// keys and the creation time answer 23514, an insert must start received and empty, and a legal write still versions the row.
    /// </summary>
    [Fact]
    public async Task Guard_RefusesEveryImmutableChangeForTheOwner()
    {
        var w = await SeedAsync();
        var artifact = await NewArtifactAsync(w, "requirement_document", "application/pdf");
        var text = Guid.CreateVersion7();
        var pdf = Guid.CreateVersion7();
        await using (var seed = await AppAsync(w))
        {
            (await seed.ExecuteAsync(InsertIntake, Row(w, "text", id: text))).ShouldBe(1);
            (await seed.ExecuteAsync(InsertIntake, Row(w, "pdf", artifact, pdf))).ShouldBe(1);
            await seed.CommitAsync();
        }

        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(w.Database));
        foreach (var set in new[]
        {
            "id = gen_random_uuid()", "org_id = gen_random_uuid()", $"project_id = '{w.Sibling}'", "feature_node_id = null",
            "kind = 'pdf'", "input_text = 'other'", "media_type = 'text/html'", "declared_bytes = 6", "created_by = null",
            "created_at = created_at - interval '1 day'", "caps = '{\"a\": 1}'",
        })
        {
            var error = await RefusesAsync(owner, Update(set), "23514", ("id", text));
            error.MessageText.ShouldContain("authoring_intake_immutable", Case.Sensitive, set);
        }

        var moved = await RefusesAsync(owner, Update("artifact_id = gen_random_uuid()"), "23514", ("id", pdf));
        moved.MessageText.ShouldContain("authoring_intake_immutable");

        var started = await RefusesAsync(
            owner, InsertIntakeWithState, "23514",
            ("id", Guid.CreateVersion7()), ("org", w.Org), ("project", w.Project), ("node", w.Node));
        started.MessageText.ShouldContain("authoring_intake_state_invalid");

        await using var app = await AppAsync(w);
        foreach (var set in new[] { "kind = 'pdf'", "input_text = 'other'", "created_at = now()", "caps = '{\"a\": 1}'" })
        {
            await RefusesAsync(app, Update(set), "42501", ("id", text));
        }

        (await app.ExecuteAsync(Update($"state = 'ready', {HashSql}"), ("id", text))).ShouldBe(1);
        (await app.ScalarAsync<string>("select caps::text from public.authoring_intake where id = @id", ("id", text))).ShouldBe("{}");

        (await owner.ScalarAsync<int>("select version from public.authoring_intake where id = @id", ("id", pdf))).ShouldBe(1);
        (await owner.ExecuteAsync(Update("state = 'extracting'"), ("id", pdf))).ShouldBe(1);
        (await owner.ScalarAsync<int>("select version from public.authoring_intake where id = @id", ("id", pdf))).ShouldBe(2);
    }

    /// <summary>
    /// The app role cannot delete an intake (no grant), the owner can, and the slot holding an intake has to go first
    /// because the slot key does not cascade.
    /// </summary>
    [Fact]
    public async Task Delete_IsTheOwnersAloneAndSlotsGoFirst()
    {
        var w = await SeedAsync();
        var artifact = await NewArtifactAsync(w, "requirement_document", "application/pdf");
        var id = Guid.CreateVersion7();
        await using (var app = await AppAsync(w))
        {
            (await app.ExecuteAsync(InsertIntake, Row(w, "pdf", artifact, id))).ShouldBe(1);
            (await app.ExecuteAsync(InsertSlot, ("org", w.Org), ("slot", 0), ("intake", id))).ShouldBe(1);
            await app.CommitAsync();
        }

        await using (var app = await AppAsync(w))
        {
            await RefusesAsync(app, "delete from public.authoring_intake where id = @id", "42501", ("id", id));
        }

        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(w.Database));
        await RefusesAsync(owner, "delete from public.authoring_intake where id = @id", "23503", ("id", id));
        (await owner.ExecuteAsync("delete from public.authoring_intake_slot where intake_id = @id", ("id", id))).ShouldBe(1);
        (await owner.ExecuteAsync("delete from public.authoring_intake where id = @id", ("id", id))).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the extraction slot table

    /// <summary>A slot is tenant-scoped and bounded; only its expiry may be renewed.</summary>
    [Fact]
    public async Task Slots_AreUniqueBoundedAndTenantScoped()
    {
        var w = await SeedAsync();
        await using var scope = await AppAsync(w);
        var one = await InsertDocumentAsync(scope, w, "pdf");
        var two = await InsertDocumentAsync(scope, w, "docx");

        (await scope.ExecuteAsync(InsertSlot, ("org", w.Org), ("slot", 0), ("intake", one))).ShouldBe(1);
        await RefusesAsync(scope, InsertSlot, "23505", ("org", w.Org), ("slot", 0), ("intake", two));
        await RefusesAsync(scope, InsertSlot, "23505", ("org", w.Org), ("slot", 1), ("intake", one));
        await RefusesAsync(scope, InsertSlot, "23514", ("org", w.Org), ("slot", 64), ("intake", two));
        await RefusesAsync(scope, InsertSlot, "23514", ("org", w.Org), ("slot", -1), ("intake", two));
        await RefusesAsync(scope, InsertSlot, "23503", ("org", w.Org), ("slot", 2), ("intake", Guid.CreateVersion7()));
        (await scope.ExecuteAsync(
            "update public.authoring_intake_slot set expires_at = now() + interval '2 minutes' where intake_id = @id",
            ("id", one))).ShouldBe(1);
        await RefusesAsync(scope, "update public.authoring_intake_slot set slot = 5 where intake_id = @id", "42501", ("id", one));
        await RefusesAsync(scope, "update public.authoring_intake_slot set lease_id = gen_random_uuid() where intake_id = @id", "42501", ("id", one));
        (await scope.ExecuteAsync(InsertSlot, ("org", w.Org), ("slot", 63), ("intake", two))).ShouldBe(1);
        (await scope.ExecuteAsync("delete from public.authoring_intake_slot where intake_id = @id", ("id", two))).ShouldBe(1);
        await scope.CommitAsync();

        foreach (var org in new Guid?[] { w.Org, null, Guid.CreateVersion7() })
        {
            await using var reader = await TenantScope.BeginAsync(fixture.AppConnectionString(w.Database), org);
            (await reader.ScalarAsync<long>("select count(*) from public.authoring_intake_slot where intake_id = @id", ("id", one)))
                .ShouldBe(org == w.Org ? 1L : 0L);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<PostgresException> RefusesAsync(
        TenantScope scope,
        string sql,
        string sqlState,
        params (string Name, object Value)[] args)
    {
        await scope.ExecuteAsync("savepoint before_probe");
        var error = await scope.CaptureErrorAsync(sql, args);
        error.SqlState.ShouldBe(sqlState, error.MessageText);
        await scope.ExecuteAsync("rollback to savepoint before_probe; release savepoint before_probe");
        return error;
    }

    private static string Update(string set) => $"update public.authoring_intake set {set} where id = @id";

    private static string MediaOf(string kind) => kind switch
    {
        "text" => "text/plain",
        "image" => "image/png",
        "pdf" => "application/pdf",
        _ => DocxType,
    };

    private static (string Name, object Value)[] With((string Name, object Value)[] args, string name, object value) =>
        [.. args.Where(arg => arg.Name != name), (name, value)];

    private static (string Name, object Value)[] Row(World w, string kind, Guid? artifact = null, Guid? id = null) =>
    [
        ("id", id ?? Guid.CreateVersion7()),
        ("org", w.Org),
        ("project", w.Project),
        ("member", w.Member),
        ("kind", kind),
        ("node", kind == "image" ? DBNull.Value : (object)w.Node),
        ("artifact", kind == "text" ? DBNull.Value : (object)(artifact ?? throw new ArgumentException("a non-text intake needs an artifact"))),
        ("text", kind == "text" ? (object)"hello" : DBNull.Value),
        ("media", MediaOf(kind)),
    ];

    private static (string Name, object Value)[] ArtifactArgs(World w, Guid id, string kind, string media) =>
    [
        ("id", id), ("org", w.Org), ("project", w.Project), ("run", w.Run), ("kind", kind), ("media", media),
        ("key", $"org/{w.Org}/project/{w.Project}/run/{w.Run}/attempt/{new string('a', 64)}/1/{id}"),
    ];

    private Task<TenantScope> AppAsync(World w) => TenantScope.BeginAsync(fixture.AppConnectionString(w.Database), w.Org);

    /// <summary>A committed artifact of the project, written by the owner (the run-bound upload flow is not under test).</summary>
    private async Task<Guid> NewArtifactAsync(World w, string kind, string media)
    {
        var id = Guid.CreateVersion7();
        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(w.Database));
        (await owner.ExecuteAsync(InsertArtifact, ArtifactArgs(w, id, kind, media))).ShouldBe(1);
        await owner.CommitAsync();
        return id;
    }

    private async Task<Guid> InsertDocumentAsync(TenantScope scope, World w, string kind)
    {
        var id = Guid.CreateVersion7();
        var artifact = await NewArtifactAsync(w, "requirement_document", MediaOf(kind));
        (await scope.ExecuteAsync(InsertIntake, Row(w, kind, artifact, id))).ShouldBe(1);
        return id;
    }

    private sealed record World(string Database, Guid Org, Guid Project, Guid Sibling, Guid Run, Guid Node, Guid Member);

    private async Task<World> SeedAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        var node = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(fixture.OwnerConnectionString(database)))
        {
            await seed.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@org, 'intake', @slug)",
                ("org", org), ("slug", Unique.Slug("intake")));
            await seed.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug, case_prefix) values (@id, @org, 'Shop', 'shop', 'SHOP')",
                ("id", project), ("org", org));
            await seed.ExecuteAsync(
                "insert into public.\"user\" (id, email) values (@user, @email)",
                ("user", user), ("email", $"intake-{user:N}@example.test"));
            await seed.ExecuteAsync(
                "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org, @user, @email, 'active')",
                ("id", member), ("org", org), ("user", user), ("email", $"intake-{user:N}@example.test"));
            await seed.ExecuteAsync(
                "insert into public.feature_node (id, org_id, project_id, kind, title, position) values (@id, @org, @project, 'module', 'Shop', 0)",
                ("id", node), ("org", org), ("project", project));
            await seed.CommitAsync();
        }

        var run = await RunRowSeeder.SeedRunAsync(fixture, database, org);
        var sibling = Guid.CreateVersion7();
        await using var second = await TenantScope.BeginAsync(fixture.OwnerConnectionString(database));
        await second.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug, case_prefix) values (@id, @org, 'Other', 'other', 'OTHR')",
            ("id", sibling), ("org", org));
        await second.CommitAsync();
        return new World(database, org, project, sibling, run, node, member);
    }
}

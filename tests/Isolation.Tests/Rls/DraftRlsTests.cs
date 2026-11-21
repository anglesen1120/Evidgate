using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The draft table as the roles that actually write it: RLS keeps one tenant blind to another's rows, the composite
/// foreign keys refuse a row that mixes tenants even when RLS is satisfied, and the guard owns the initial insert
/// state, the status machine, the immutable identity, the locked content and the one-step version. Every refusal
/// asserts the SQLSTATE and the server's own message or constraint, paired with the nearest legal statement.
/// </summary>
[Collection(IsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class DraftRlsTests
{
    private static readonly Guid OrgA = Guid.Parse("44444444-4444-7444-8444-4444444444a1");
    private static readonly Guid OrgB = Guid.Parse("44444444-4444-7444-8444-4444444444b2");
    private static readonly Guid ProjectA1 = Guid.Parse("44444444-4444-7444-8444-4444444444c1");
    private static readonly Guid ProjectA2 = Guid.Parse("44444444-4444-7444-8444-4444444444c2");
    private static readonly Guid ProjectB1 = Guid.Parse("44444444-4444-7444-8444-4444444444c3");
    private static readonly Guid MemberA = Guid.Parse("44444444-4444-7444-8444-4444444444d1");
    private static readonly Guid MemberA2 = Guid.Parse("44444444-4444-7444-8444-4444444444d2");
    private static readonly Guid MemberB = Guid.Parse("44444444-4444-7444-8444-4444444444d3");
    private static readonly Guid UserA = Guid.Parse("44444444-4444-7444-8444-4444444444e1");
    private static readonly Guid UserA2 = Guid.Parse("44444444-4444-7444-8444-4444444444e3");
    private static readonly Guid UserB = Guid.Parse("44444444-4444-7444-8444-4444444444e2");
    private static readonly Guid CaseA = Guid.Parse("44444444-4444-7444-8444-4444444444f1");
    private static readonly Guid CaseB = Guid.Parse("44444444-4444-7444-8444-4444444444f2");

    private readonly PostgresIsolationFixture _fixture;
    private Task<string>? _world;

    public DraftRlsTests(PostgresIsolationFixture fixture) => _fixture = fixture;

    private Task<string> WorldAsync() => _world ??= SeedAsync();

    private async Task<string> SeedAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await _fixture.ExecuteAsync(database, $"""
            insert into public.organization (id, name, slug) values
              ('{OrgA}', 'A', 'dr-org-a'), ('{OrgB}', 'B', 'dr-org-b');
            insert into public."user" (id, email) values
              ('{UserA}', 'a@dr.test'), ('{UserA2}', 'a2@dr.test'), ('{UserB}', 'b@dr.test');
            insert into public.project (id, org_id, name, slug, case_prefix) values
              ('{ProjectA1}', '{OrgA}', 'A1', 'dra1', 'DR'),
              ('{ProjectA2}', '{OrgA}', 'A2', 'dra2', 'DS'),
              ('{ProjectB1}', '{OrgB}', 'B1', 'drb1', 'DT');
            insert into public.membership (id, org_id, user_id, invited_email, status) values
              ('{MemberA}', '{OrgA}', '{UserA}', 'a@dr.test', 'active'),
              ('{MemberA2}', '{OrgA}', '{UserA2}', 'a2@dr.test', 'active'),
              ('{MemberB}', '{OrgB}', '{UserB}', 'b@dr.test', 'active');
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) values
              ('{CaseA}', '{OrgA}', '{ProjectA1}', uuidv7(), 'a1', 'pytest', 'aaaaaaa'),
              ('{CaseB}', '{OrgB}', '{ProjectB1}', uuidv7(), 'b1', 'pytest', 'bbbbbbb');
            """);
        return database;
    }

    private Task<TenantScope> AppAsync(string database, Guid org) =>
        TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

    private static string Content(string path = "tests/one.ts", string sha = "sha256:" + "a") =>
        $$"""{"files":[{"path":"{{path}}","body_sha":"{{sha}}","body_ref":"drafts/one.ts"}]}""";

    /// <summary>
    /// Files a draft at an arbitrary status on the owner connection with the user triggers off: the guard rightly
    /// refuses most of these inserts and the status is a precondition here, not the fact under test.
    /// </summary>
    private async Task<Guid> SeedAsync(
        string database,
        Guid org = default,
        Guid? project = null,
        Guid? author = null,
        string status = "draft",
        string? content = null)
    {
        var id = Guid.CreateVersion7();
        var orgId = org == default ? OrgA : org;
        var projectId = project ?? (orgId == OrgA ? ProjectA1 : ProjectB1);
        var memberId = author ?? (orgId == OrgA ? MemberA : MemberB);

        await _fixture.ExecuteAsync(database, $"""
            set session_replication_role = replica;
            insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, status, content)
            values ('{id}', '{orgId}', '{projectId}', '{memberId}', 'template', 'abcdef1', '{status}', '{content ?? Content()}');
            """);
        return id;
    }

    [Fact]
    public async Task Draft_Rls_CrossTenant_ZeroRows_AndInsertForTheOtherOrgRefused()
    {
        var database = await WorldAsync();
        var aDraft = await SeedAsync(database);

        await using (var b = await AppAsync(database, OrgB))
        {
            (await b.ScalarAsync<int>("select count(*)::int from public.draft")).ShouldBe(
                0, "org B sees none of org A's drafts");
            (await b.ScalarAsync<int>($"select count(*)::int from public.draft where org_id = '{OrgA}'")).ShouldBe(
                0, "even naming the other org's column reads nothing");
            (await b.ScalarAsync<int>($"select count(*)::int from public.draft where id = '{aDraft}'")).ShouldBe(
                0, "the draft's id is not a read key across tenants");

            var refused = await b.CaptureErrorAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
                values (uuidv7(), '{OrgA}', '{ProjectA1}', '{MemberA}', 'template', 'abcdef1', '{Content()}')
                """);
            refused.SqlState.ShouldBe("42501", "the with-check refuses a row filed under another org");
        }

        // Positive control: the same shape filed under the scope's own org is accepted, so the tenant is the only
        // difference between the refusal and the write.
        await using (var allowed = await AppAsync(database, OrgB))
        {
            var inserted = await allowed.ExecuteAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
                values (uuidv7(), '{OrgB}', '{ProjectB1}', '{MemberB}', 'template', 'abcdef1', '{Content()}')
                """);
            inserted.ShouldBe(1);
        }

        await using var a = await AppAsync(database, OrgA);
        (await a.ScalarAsync<int>("select count(*)::int from public.draft")).ShouldBe(1, "org A still reads its own row");
    }

    [Fact]
    public async Task Draft_CompositeForeignKeys_RefuseAProjectOrCaseOfAnotherTenant()
    {
        var database = await WorldAsync();

        await using (var a = await AppAsync(database, OrgA))
        {
            var foreignProject = await a.CaptureErrorAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
                values (uuidv7(), '{OrgA}', '{ProjectB1}', '{MemberA}', 'template', 'abcdef1', '{Content()}')
                """);
            foreignProject.SqlState.ShouldBe("23503", "a draft cannot hang a project of another tenant");
            foreignProject.ConstraintName.ShouldBe("draft_project_fk");
        }

        await using (var allowedProject = await AppAsync(database, OrgA))
        {
            (await allowedProject.ExecuteAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
                values (uuidv7(), '{OrgA}', '{ProjectA1}', '{MemberA}', 'template', 'abcdef1', '{Content()}')
                """)).ShouldBe(1);
        }

        await using (var a = await AppAsync(database, OrgA))
        {
            var foreignCase = await a.CaptureErrorAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content, linked_case_id)
                values (uuidv7(), '{OrgA}', '{ProjectA1}', '{MemberA}', 'template', 'abcdef1', '{Content()}', '{CaseB}')
                """);
            foreignCase.SqlState.ShouldBe("23503", "the linked case is resolved inside the draft's own tenant");
            foreignCase.ConstraintName.ShouldBe("draft_linked_case_fk");
        }

        await using (var a = await AppAsync(database, OrgA))
        {
            // Control: the same row with the tenant's own case passes, so the composite pair is what refused.
            var ownCase = await a.ExecuteAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content, linked_case_id)
                values (uuidv7(), '{OrgA}', '{ProjectA1}', '{MemberA}', 'template', 'abcdef1', '{Content()}', '{CaseA}')
                """);
            ownCase.ShouldBe(1);
        }
    }

    [Theory]
    [InlineData("draft", "dry_running")]
    [InlineData("dry_running", "dry_passed")]
    [InlineData("dry_running", "dry_failed")]
    [InlineData("dry_passed", "pr_open")]
    [InlineData("dry_failed", "dry_running")]
    [InlineData("dry_failed", "pr_open")]
    [InlineData("pr_open", "draft")]
    [InlineData("pr_open", "merged")]
    [InlineData("pr_open", "closed")]
    [InlineData("merged", "merged")]
    public async Task DraftGuard_AllowsEveryDeclaredTransition(string from, string to)
    {
        var database = await WorldAsync();
        var draft = await SeedAsync(database, status: from);

        await using var scope = await AppAsync(database, OrgA);
        (await scope.ExecuteAsync(
            $"update public.draft set status = '{to}' where id = @d", ("d", draft))).ShouldBe(1, $"{from} -> {to}");
        (await scope.ScalarAsync<string>("select status from public.draft where id = @d", ("d", draft))).ShouldBe(to);
    }

    [Theory]
    [InlineData("draft", "merged")]
    [InlineData("draft", "dry_passed")]
    [InlineData("dry_running", "pr_open")]
    [InlineData("dry_passed", "closed")]
    [InlineData("merged", "pr_open")]
    [InlineData("merged", "draft")]
    [InlineData("closed", "draft")]
    public async Task DraftGuard_RefusesATransitionTheMachineDoesNotDeclare(string from, string to)
    {
        var database = await WorldAsync();
        var draft = await SeedAsync(database, status: from);
        var before = await _fixture.ScalarAsync<int>(database, $"select version from public.draft where id = '{draft}'");

        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                $"update public.draft set status = '{to}' where id = @d", ("d", draft));
            error.SqlState.ShouldBe("23514", $"{from} -> {to} is not an edge");
            error.MessageText.ShouldBe($"draft cannot move from {from} to {to}");
        }

        (await _fixture.ScalarAsync<string>(database, $"select status from public.draft where id = '{draft}'"))
            .ShouldBe(from, "a refused move leaves the status");
        (await _fixture.ScalarAsync<int>(database, $"select version from public.draft where id = '{draft}'"))
            .ShouldBe(before, "and the version");

        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ScalarAsync<int>(
            "update public.draft set status = @s where id = @d returning version", ("s", from), ("d", draft)))
            .ShouldBe(before + 1, "keeping the current status is legal");
    }

    [Theory]
    [InlineData("project_id", "other-project")]
    [InlineData("kind", "ai")]
    [InlineData("base_commit", "fedcba9")]
    [InlineData("author_membership_id", "other-membership")]
    [InlineData("created_at", "earlier")]
    public async Task DraftGuard_RefusesAnIdentityChange(string member, string token)
    {
        var database = await WorldAsync();
        var draft = await SeedAsync(database);
        object value = token switch
        {
            "other-project" => ProjectA2,
            "other-membership" => MemberA2,
            "earlier" => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => token,
        };

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                $"update public.draft set {member} = @v where id = @d", ("v", value), ("d", draft));
            error.SqlState.ShouldBe("23514", member);
            error.MessageText.ShouldBe("draft identity is immutable");
        }

        // The owner must be able to keep the same identity; the guard rejects a change, not any write.
        await using var allowed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), OrgA);
        (await allowed.ExecuteAsync(
            $"update public.draft set {member} = {member} where id = @d", ("d", draft))).ShouldBe(1);
    }

    [Theory]
    [InlineData("project_id")]
    [InlineData("kind")]
    [InlineData("base_commit")]
    [InlineData("author_membership_id")]
    [InlineData("created_at")]
    public async Task Draft_AppRole_RefusesIdentityColumnUpdates(string member)
    {
        var database = await WorldAsync();
        var draft = await SeedAsync(database);

        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                $"update public.draft set {member} = {member} where id = @d", ("d", draft));
            error.SqlState.ShouldBe("42501", member);
        }

        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(
            "update public.draft set allow_unverified = true where id = @d", ("d", draft))).ShouldBe(1);
    }

    [Fact]
    public async Task DraftGuard_RefusesContentChangeOnceTheDraftLeftDraftState()
    {
        var database = await WorldAsync();
        var draft = await SeedAsync(database);
        var dry = await SeedAsync(database, status: "dry_passed");

        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(
                "update public.draft set content = @c::jsonb where id = @d", ("c", Content("tests/two.ts")), ("d", dry));
            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldBe("draft content is editable only in draft state");
        }

        // Control: at status 'draft' the same content change is accepted, and the version steps once.
        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(
            "update public.draft set content = @c::jsonb where id = @d", ("c", Content("tests/two.ts")), ("d", draft))).ShouldBe(1);
        (await allowed.ScalarAsync<int>("select version from public.draft where id = @d", ("d", draft))).ShouldBe(2);
    }

    [Fact]
    public async Task Draft_ContentCheck_RefusesNonObjectAndTheByteCeiling()
    {
        var database = await WorldAsync();

        await using (var scope = await AppAsync(database, OrgA))
        {
            var notObject = await scope.CaptureErrorAsync($"""
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
                values (uuidv7(), '{OrgA}', '{ProjectA1}', '{MemberA}', 'template', 'abcdef1', '[]')
                """);
            notObject.SqlState.ShouldBe("23514");
            notObject.ConstraintName.ShouldBe("draft_content_check");
        }

        await using (var scope = await AppAsync(database, OrgA))
        {
            var oversized = await scope.CaptureErrorAsync(
                """
                insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
                values (uuidv7(), @o, @p, @m, 'template', 'abcdef1', jsonb_build_object('files', jsonb_build_array(
                    jsonb_build_object('path', 'tests/big.ts', 'body_sha', 'sha256:' || repeat('a', 64),
                                        'body_ref', repeat('x', 270000)))))
                """,
                ("o", OrgA), ("p", ProjectA1), ("m", MemberA));
            oversized.SqlState.ShouldBe("23514");
            oversized.ConstraintName.ShouldBe("draft_content_check");
        }

        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync($"""
            insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, content)
            values (uuidv7(), '{OrgA}', '{ProjectA1}', '{MemberA}', 'template', 'abcdef1', '{Content()}')
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task Draft_AppRole_HasNoDelete_AndVersionIncrementsByOnePerUpdate()
    {
        var database = await WorldAsync();
        var draft = await SeedAsync(database);

        await using (var refused = await AppAsync(database, OrgA))
        {
            var delete = await refused.CaptureErrorAsync("delete from public.draft where id = @d", ("d", draft));
            delete.SqlState.ShouldBe("42501", "the runtime role has no DELETE on drafts");
        }

        await using var scope = await AppAsync(database, OrgA);

        (await scope.ScalarAsync<int>(
            "update public.draft set allow_unverified = true where id = @d returning version", ("d", draft)))
            .ShouldBe(2, "the first update steps the version exactly once");
        (await scope.ScalarAsync<int>(
            "update public.draft set unverified_reason = 'checked by hand' where id = @d returning version", ("d", draft)))
            .ShouldBe(3, "so does the second");
    }

    [Theory]
    [InlineData("dry_running", 1)]
    [InlineData("merged", 1)]
    [InlineData("draft", 0)]
    [InlineData("draft", 2)]
    public async Task DraftGuard_RefusesInsertOutsideInitialState(string status, int version)
    {
        var database = await WorldAsync();
        const string insert = """
            insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, status, version, content)
            values (uuidv7(), @o, @p, @m, 'template', 'abcdef1', @s, @v, @c::jsonb)
            """;

        await using (var scope = await AppAsync(database, OrgA))
        {
            var error = await scope.CaptureErrorAsync(insert,
                ("o", OrgA), ("p", ProjectA1), ("m", MemberA), ("s", status), ("v", version), ("c", Content()));
            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldBe("draft must start in draft state at version 1");
        }

        await using var allowed = await AppAsync(database, OrgA);
        (await allowed.ExecuteAsync(insert,
            ("o", OrgA), ("p", ProjectA1), ("m", MemberA), ("s", "draft"), ("v", 1), ("c", Content()))).ShouldBe(1);
    }
}

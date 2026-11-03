using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA exploratory pass for 07.3.1-T2 (independent of the implementer). Boundary, negative and
/// exploratory cases the implementer's suite does not pin: owner membership status, reason length
/// in characters versus UTF-16 units, the <c>expires_at</c> offset, the missing <c>Idempotency-Key</c>
/// on both mutations, re-quarantine after removal, and a malformed entry id. Helpers are copied from
/// <c>QuarantineTests</c> because those are private there.
/// </summary>
[Trait("Category", "Docker")]
public sealed class QuarantineExploratoryTests
{
    private readonly PostgresTenancyFixture _fixture;

    public QuarantineExploratoryTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Case,
        Guid Maintainer,
        Guid MaintainerMembership) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public Action<HttpRequestHeaders> AsMaintainer => TenancyApiFactory.Resolved(user: Maintainer, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        var caseId = Guid.CreateVersion7();
        var maintainer = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // 0044's `membership_user_fk` makes the membership's user_id name a real account.
            await scope.ExecuteAsync(
                """insert into public."user" (id, email) values (@id, @email)""",
                ("id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            await scope.ExecuteAsync(
                "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
                ("id", membership), ("org_id", org), ("user_id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            await scope.ExecuteAsync(
                """
                insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
                values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
                """,
                ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()), ("name", "case 0"));
            await scope.CommitAsync();
        }

        var role = await _fixture.ScalarAsync<Guid>(database, "select id from public.role where code = 'test_maintainer' and org_id is null");
        var granted = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/memberships/{membership}/roles",
            TenancyApiFactory.Resolved(user: user, orgHeader: org),
            new { role_id = role },
            NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return new World(api, client, database, user, org, project, caseId, maintainer, membership);
    }

    private static object Body(
        World world,
        string reason = "known failing case",
        DateTimeOffset? expiresAt = null,
        Guid? owner = null) => new
        {
            project_id = world.Project,
            case_id = world.Case,
            owner = owner ?? world.MaintainerMembership,
            reason,
            expires_at = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
        };

    private static Task<ApiAnswer> CreateAsync(World world, object body, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/quarantine", world.AsMaintainer, body, key ?? NewKey());

    /// <summary>A membership row in the org with the given status, inserted as the owner role (no role grant).</summary>
    private async Task<Guid> InsertMembershipAsync(World world, string status)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{world.Org}', '{userId}', 'member-{userId:N}@example.test', '{status}')
            """);

        return membershipId;
    }

    /// <summary>
    /// Inserts an <c>active</c> <c>policy_revision</c> carrying <paramref name="criticalCases"/> as the owner role:
    /// activation merged with 07.1.1-T4 and the API refuses to activate a revision whose critical case is under an
    /// active quarantine (Q2), so the state is seeded directly. Copied from <c>QuarantineTests.InsertRevisionAsync</c>.
    /// </summary>
    private async Task InsertActiveRevisionAsync(World world, IReadOnlyList<string> criticalCases)
    {
        var id = Guid.CreateVersion7();
        var body = JsonSerializer.Serialize(new { critical_cases = criticalCases });
        var hash = "sha256:" + new string('a', 64);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by)
            values ('{id}', '{world.Org}', '{world.Project}', 1, '{hash}', '{body}'::jsonb, 'active', '{world.User}')
            """)).ShouldBe(1);
        await owner.CommitAsync();
    }

    /// <summary>A string of <paramref name="codePoints"/> emoji: each is one character but two UTF-16 units.</summary>
    private static string Emoji(int codePoints)
    {
        var text = new StringBuilder(codePoints * 2);
        for (var i = 0; i < codePoints; i++)
        {
            text.Append("\U0001F600");
        }

        return text.ToString();
    }

    // ------------------------------------------------------------------ owner membership status

    /// <summary>
    /// Plan §4 owner row: "member accountable for the quarantine". OwnerExists checks only org and id,
    /// so a non-active membership is accepted. Expected 422 under the plan's wording; a failure here is the defect.
    /// </summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("removed")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task Owner_WhoIsNotAnActiveMember_IsRefused422(string status)
    {
        await using var world = await StartAsync();
        var owner = await InsertMembershipAsync(world, status);

        var answer = await CreateAsync(world, Body(world, owner: owner));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ reason length

    /// <summary>
    /// Plan says the reason is 1–2000 characters; the DB checks <c>char_length</c> (code points). The API checks
    /// <c>string.Length</c> (UTF-16 units). 2000 emoji is 2000 characters but 4000 units, so a correct server accepts it.
    /// Expected 201; a 422 here is a defect (the API is stricter than the contract).
    /// </summary>
    [Fact]
    public async Task Reason_2000Emoji_IsAccepted_BecauseTheLimitIsInCharacters()
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, Body(world, reason: Emoji(2000)));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry where char_length(reason) = 2000")).ShouldBe(1);
    }

    /// <summary>Control: 1000 emoji is 2000 units and 1000 characters, accepted under both counts.</summary>
    [Fact]
    public async Task Reason_1000Emoji_IsAccepted_ControlCase()
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, Body(world, reason: Emoji(1000)));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
    }

    /// <summary>Control: 2001 emoji is over the limit under both counts; expected 422.</summary>
    [Fact]
    public async Task Reason_2001Emoji_IsRefused422_ControlCase()
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, Body(world, reason: Emoji(2001)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
    }

    /// <summary>
    /// A lone surrogate cannot be stored in PostgreSQL text (it would be replaced by U+FFFD on the wire), so it is
    /// refused, never a 500. The raw JSON carries the <c>\ud800</c> escape because a <c>JsonContent</c> round-trip
    /// would have replaced the surrogate before it left the client; the JSON binding refuses it with 400 before the
    /// handler, and the handler's own <c>HasUnstorableText</c> rule is the defence in depth behind that.
    /// </summary>
    [Fact]
    public async Task Reason_WithALoneSurrogate_IsRefused_NotA500()
    {
        await using var world = await StartAsync();
        var expires = DateTimeOffset.UtcNow.AddDays(7).ToString("O");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/quarantine");
        world.AsMaintainer(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(
            $$"""{"project_id":"{{world.Project}}","case_id":"{{world.Case}}","owner":"{{world.MaintainerMembership}}","reason":"a\ud800b","expires_at":"{{expires}}"}""",
            Encoding.UTF8,
            "application/json");

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var status = (int)response.StatusCode;
        (status is 400 or 422).ShouldBeTrue(text);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ expires_at offset

    /// <summary>
    /// A non-UTC offset must store the same instant. 2030-01-01 12:00+07:00 is 05:00 UTC.
    /// Whole seconds keep the comparison clear of microsecond truncation.
    /// </summary>
    [Fact]
    public async Task ExpiresAt_WithANonUtcOffset_IsStoredAsTheSameInstant()
    {
        await using var world = await StartAsync();
        var expires = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.FromHours(7));

        var answer = await CreateAsync(world, Body(world, expiresAt: expires));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var stored = await _fixture.ScalarAsync<DateTime>(world.Database, "select expires_at from public.quarantine_entry");
        stored.ShouldBe(expires.UtcDateTime);
    }

    // ------------------------------------------------------------------ idempotency

    /// <summary>Plan §8 idempotency: a mutation without <c>Idempotency-Key</c> is refused by the pipeline (400) and writes nothing.</summary>
    [Fact]
    public async Task Create_WithoutIdempotencyKey_Is400_AndWritesNothing()
    {
        await using var world = await StartAsync();

        var answer = await world.Client.SendAsync(
            HttpMethod.Post, "/v1/quarantine", world.AsMaintainer, Body(world), idempotencyKey: null);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.Body.GetRawText().ShouldContain("Idempotency-Key");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    /// <summary>The same rule on DELETE: no key is 400, and the active entry is left active.</summary>
    [Fact]
    public async Task Remove_WithoutIdempotencyKey_Is400_AndLeavesTheEntryActive()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Body(world));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var entry = Guid.Parse(created.Text("id")!);

        var answer = await world.Client.SendAsync(
            HttpMethod.Delete, $"/v1/quarantine/{entry}", world.AsMaintainer, null, idempotencyKey: null);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{entry}'")).ShouldBe("active");
    }

    // ------------------------------------------------------------------ lifecycle and re-quarantine

    /// <summary>After a removal the partial unique index no longer holds the case, so a new active entry is allowed.</summary>
    [Fact]
    public async Task Requarantine_AfterRemove_Is201_AndOnlyTheNewEntryIsActive()
    {
        await using var world = await StartAsync();
        var first = await CreateAsync(world, Body(world));
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var firstId = Guid.Parse(first.Text("id")!);

        var removed = await world.Client.SendAsync(HttpMethod.Delete, $"/v1/quarantine/{firstId}", world.AsMaintainer, null, NewKey());
        removed.Status.ShouldBe(HttpStatusCode.OK, removed.ErrorMessage);

        var second = await CreateAsync(world, Body(world));

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry where state = 'active'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry where state = 'removed'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ malformed input

    /// <summary>A path id that is not a GUID must not succeed and must not produce a 5xx.</summary>
    [Fact]
    public async Task Remove_WithAMalformedId_IsNotSuccessAndNotA5xx()
    {
        await using var world = await StartAsync();

        var answer = await world.Client.SendAsync(
            HttpMethod.Delete, "/v1/quarantine/not-a-guid", world.AsMaintainer, null, NewKey());

        answer.Status.ShouldNotBe(HttpStatusCode.OK);
        ((int)answer.Status).ShouldBeLessThan(500);
    }

    // ------------------------------------------------------------------ critical list: UUID text forms (F1)

    /// <summary>
    /// The critical-list match must parse each item as a UUID, not compare canonical text: an active revision that
    /// lists the case in the no-hyphen (N), braced (B), whitespace-padded (D) or upper-case form still refuses the
    /// write with 422 <c>critical_case_not_quarantinable</c> and writes no row (T23, AC4).
    /// </summary>
    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("D")]
    [InlineData("upper")]
    public async Task CriticalCase_InANonCanonicalUuidTextForm_IsRefused422_AndWritesNoRow(string form)
    {
        await using var world = await StartAsync();
        var text = form switch
        {
            "N" => world.Case.ToString("N"),
            "B" => world.Case.ToString("B"),
            "D" => $"  {world.Case:D}  ",
            "upper" => world.Case.ToString("D").ToUpperInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(form)),
        };
        await InsertActiveRevisionAsync(world, [text]);

        var answer = await CreateAsync(world, Body(world));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("critical_case_not_quarantinable");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    /// <summary>A critical item that is not a UUID never parses, so it never matches and the write is allowed.</summary>
    [Fact]
    public async Task CriticalList_WithANonUuidItem_DoesNotMatch_AndTheWriteIs201()
    {
        await using var world = await StartAsync();
        await InsertActiveRevisionAsync(world, ["checkout/guest"]);

        var answer = await CreateAsync(world, Body(world));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
    }

    // ------------------------------------------------------------------ service principal (SEC-F3)

    /// <summary>
    /// A service principal carries no user id, so it can never remove an entry: the handler refuses it with the
    /// same 403 <c>forbidden</c> the create uses. The real evaluator refuses <c>quarantine:write</c> to a service
    /// token before the handler, so this test reaches the handler through the grant-all evaluator (the existing
    /// pattern for handler-level rules) and pins the defence-in-depth check itself.
    /// </summary>
    [Fact]
    public async Task Remove_ByAServicePrincipalWithoutUserId_Is403()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var user = Guid.CreateVersion7();
        var orgAnswer = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Caller(user: user, email: "acme@example.test"), new { name = "acme", slug = "acme" }, NewKey());
        orgAnswer.Status.ShouldBe(HttpStatusCode.Created, orgAnswer.ErrorMessage);
        var org = orgAnswer.Id();

        var projectAnswer = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Caller(org: org, user: user), new { name = "checkout", slug = "checkout" }, NewKey());
        projectAnswer.Status.ShouldBe(HttpStatusCode.Created, projectAnswer.ErrorMessage);
        var project = projectAnswer.Id();

        var caseId = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // 0044's `membership_user_fk` makes the membership's user_id name a real account.
            await scope.ExecuteAsync(
                """insert into public."user" (id, email) values (@id, @email)""",
                ("id", owner), ("email", $"m-{owner:N}@example.test"));
            await scope.ExecuteAsync(
                "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
                ("id", membership), ("org_id", org), ("user_id", owner), ("email", $"m-{owner:N}@example.test"));
            await scope.ExecuteAsync(
                """
                insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
                values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
                """,
                ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()), ("name", "case 0"));
            await scope.CommitAsync();
        }

        var member = TenancyApiFactory.Caller(org: org, user: user);
        var created = await client.SendAsync(
            HttpMethod.Post,
            "/v1/quarantine",
            member,
            new { project_id = project, case_id = caseId, owner = membership, reason = "known failing case", expires_at = DateTimeOffset.UtcNow.AddDays(7) },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var entry = created.Id();

        var service = TenancyApiFactory.Caller(org: org, project: project, serviceScope: project, servicePermissions: ["quarantine:write"]);
        var answer = await client.SendAsync(HttpMethod.Delete, $"/v1/quarantine/{entry}", service, null, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        (await _fixture.ScalarAsync<string>(database, $"select state from public.quarantine_entry where id = '{entry}'")).ShouldBe("active");
    }
}

using System.Globalization;
using System.Net;
using Evidgate.Modules.Policy.Application.Queries;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class PolicyEndpointTests
{
    /// <summary>
    /// 00 §5.7 P4: the revision list walks the unique <c>(org_id, project_id, revision_no)</c> btree. The target
    /// project is seeded sparsely and the rest of the table densely, then <c>ANALYZE</c>d, so the planner prefers
    /// the index without <c>enable_seqscan = off</c> (which the 07.1.1-T3 contract forbids).
    /// </summary>
    [Fact]
    public async Task ListPolicyRevisions_UsesTheRevisionNoIndex()
    {
        await using var world = await StartAsync();
        var otherProject = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.project (id, org_id, name, slug)
            values ('{otherProject}', '{world.Org}', 'other', 'other');
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', g,
                   'sha256:' || lpad(to_hex(g), 64, '0'), jsonb_build_object(), gen_random_uuid()
              from generate_series(1, 100) g;
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select gen_random_uuid(), '{world.Org}', '{otherProject}', g,
                   'sha256:' || lpad(to_hex(g), 64, '0'), jsonb_build_object(), gen_random_uuid()
              from generate_series(1, 50000) g;
            analyze public.policy_revision;
            """);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // The production list statements (PolicyRevisionListSql), with the handler's own parameter names bound.
        var firstPage = await ExplainAsync(connection, transaction, PolicyRevisionListSql.FirstPage, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("project_id", world.Project);
            command.Parameters.AddWithValue("limit", 51);
        });
        firstPage.ShouldContain("policy_revision_revision_no_key");

        var nextPage = await ExplainAsync(connection, transaction, PolicyRevisionListSql.NextPage, command =>
        {
            command.Parameters.AddWithValue("org_id", world.Org);
            command.Parameters.AddWithValue("project_id", world.Project);
            command.Parameters.AddWithValue("cursor_revision_no", 50);
            command.Parameters.AddWithValue("cursor_id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("limit", 51);
        });
        nextPage.ShouldContain("policy_revision_revision_no_key");
    }

    /// <summary>Runs <c>EXPLAIN (FORMAT JSON)</c> over one production statement with its real parameters bound.</summary>
    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> bind)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        bind(command);

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // ------------------------------------------------------------------ transport cap (00 §8.6, s9-plan)

    /// <summary>
    /// The 64 KiB transport cap: a body of exactly <c>MaxPolicyBytes</c> is read and stored, one byte more is
    /// refused <c>413</c> before parsing, and the streamed (no <c>Content-Length</c>) path caps the same way.
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_BodyAtTheCap_IsAccepted_OneByteOver_Is413()
    {
        await using var world = await StartAsync();

        var atCap = CapBody();
        atCap.Length.ShouldBe(64 * 1024);

        var accepted = await CreateAsync(world, atCap);
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await RevisionCountAsync(world)).ShouldBe(1);

        var overCap = await CreateAsync(world, atCap + " ");
        overCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overCap.ErrorCode.ShouldBe("payload_too_large");

        // The same one-byte-over body with no Content-Length: the endpoint must cap the stream it reads.
        var streamed = await SendRawAsync(
            world.Client, HttpMethod.Post, $"/v1/projects/{world.Project}/policies", world.Admin, atCap + " ", chunked: true);
        streamed.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        streamed.ErrorCode.ShouldBe("payload_too_large");

        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    /// <summary>A schema-valid <c>policy-revision.v1</c> body of exactly 64 KiB (all-ASCII, so chars are bytes).</summary>
    private static string CapBody()
    {
        var items = new List<string>();
        for (var i = 0; i < 322; i++)
        {
            items.Add(i.ToString("D3", CultureInfo.InvariantCulture) + new string('a', 197));
        }

        items.Add("zzz" + new string('b', 143));

        return "{\"required_suites\":[" + string.Join(",", items.Select(item => "\"" + item + "\"")) + "]}";
    }

    // ------------------------------------------------------------------ malformed bodies

    /// <summary>An empty body and a non-object root are <c>422 policy_invalid</c>, never a 500 and never a row.</summary>
    [Fact]
    public async Task CreatePolicyRevision_EmptyBody_AndArrayRoot_Are422()
    {
        await using var world = await StartAsync();

        var empty = await CreateAsync(world, string.Empty);
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        empty.ErrorCode.ShouldBe("policy_invalid");
        Detail(empty).GetProperty("reason").GetString().ShouldBe("invalid_json");

        var array = await CreateAsync(world, "[1,2]");
        array.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        array.ErrorCode.ShouldBe("policy_invalid");

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A lone UTF-16 surrogate, a duplicate member name and numbers a double cannot hold exactly are all
    /// <c>422 policy_invalid</c> before any statement (I-JSON, RFC 7493 §2.2; RFC 8785 §3.2.3).
    /// </summary>
    [Fact]
    public async Task CreatePolicyRevision_LoneSurrogate_DuplicateKeys_AndInexactNumbers_Are422()
    {
        await using var world = await StartAsync();

        var surrogate = await CreateAsync(world, """{"required_suites":["\ud800"]}""");
        surrogate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        surrogate.ErrorCode.ShouldBe("policy_invalid");
        Detail(surrogate).GetProperty("reason").GetString().ShouldBe("invalid_value");

        var duplicate = await CreateAsync(world, """{"flaky_budget":3,"flaky_budget":4}""");
        duplicate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        duplicate.ErrorCode.ShouldBe("policy_invalid");

        foreach (var body in new[]
        {
            """{"flaky_budget":1e400}""",
            """{"flaky_budget":0.1000000000000000001}""",
            """{"flaky_budget":9007199254740993}""",
        })
        {
            var inexact = await CreateAsync(world, body);
            inexact.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{body}: {inexact.ErrorMessage}");
            inexact.ErrorCode.ShouldBe("policy_invalid");
        }

        (await RevisionCountAsync(world)).ShouldBe(0);
    }

    /// <summary>The media type is not part of the contract (00 §9.2 has no 415): the raw body is read regardless.</summary>
    [Fact]
    public async Task CreatePolicyRevision_WrongContentType_StillReadsTheRawBody()
    {
        await using var world = await StartAsync();

        var answer = await SendRawAsync(
            world.Client, HttpMethod.Post, $"/v1/projects/{world.Project}/policies", world.Admin, BodyA, contentType: "text/plain");

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ If-Match

    /// <summary>A malformed <c>If-Match</c>, and one naming a head when no revision exists, are both 412.</summary>
    [Fact]
    public async Task CreatePolicyRevision_MalformedIfMatch_AndIfMatchOnTheFirstRevision_Are412()
    {
        await using var world = await StartAsync();

        var malformed = await CreateAsync(world, BodyA, ifMatch: "not-a-revision-id");
        malformed.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        malformed.ErrorCode.ShouldBe("precondition_failed");
        Detail(malformed).GetProperty("reason").GetString().ShouldBe("invalid");

        // No revision exists yet, so any cached head is stale: 412, nothing written.
        var first = await CreateAsync(world, BodyA, ifMatch: $"\"{Guid.CreateVersion7()}\"");
        first.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        first.ErrorCode.ShouldBe("precondition_failed");
        Detail(first).GetProperty("reason").GetString().ShouldBe("stale");
        (await RevisionCountAsync(world)).ShouldBe(0);

        // Without the header the same body is the first revision.
        (await CreateAsync(world, BodyA)).Status.ShouldBe(HttpStatusCode.Created);
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ list bounds

    /// <summary>The <c>limit</c> bounds are inclusive (1..200) and a cursor this endpoint did not issue is 422.</summary>
    [Fact]
    public async Task ListPolicyRevisions_LimitBounds_AndCursorTampering_Are422()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit=1", world.Admin))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit=200", world.Admin))
            .Status.ShouldBe(HttpStatusCode.OK);

        foreach (var limit in new[] { "0", "201", "-1", "+1", "1.0", "abc" })
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?limit={Uri.EscapeDataString(limit)}", world.Admin);
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"limit={limit}");
        }

        foreach (var cursor in new[]
        {
            "not-a-cursor",
            $"00000000-{Guid.CreateVersion7():D}",
            $"zzzzzzzz-{Guid.CreateVersion7():D}",
            "00000001-not-a-uuid",
        })
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions?cursor={Uri.EscapeDataString(cursor)}", world.Admin);
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"cursor={cursor}");
        }
    }

    // ------------------------------------------------------------------ read permission

    /// <summary>A credential that does not claim <c>policy:read</c> is refused 403 before the handler (C2 stage 4).</summary>
    [Fact]
    public async Task Read_WithoutPolicyRead_Is403()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, BodyA);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var ci = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]);

        var one = await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}", ci);
        one.Status.ShouldBe(HttpStatusCode.Forbidden);
        one.ErrorCode.ShouldBe("forbidden");

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/policies/{created.Id()}/revisions", ci))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }
}

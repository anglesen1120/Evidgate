using Npgsql;
using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The replay and key side of the two tenant decisions, over the real host: a repeated key answers the stored
/// 200 without a second write or audit row, a key reused for another grant is a conflict that changes nothing,
/// and a request with no key is refused before any write and then succeeds once the key is supplied.
/// </summary>
public sealed partial class SupportGrantTests
{
    [Fact]
    public async Task Approve_SameKeyReplay_AnswersStored200AndWritesNothingAgain()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");
        var key = NewKey();

        var first = await ExchangeAsync(client, HttpMethod.Post, ApproveUrl(grant), Admin(founder, org), key);
        first.Status.ShouldBe(HttpStatusCode.OK, "the control: the key's first use decides the grant");
        first.Body.ShouldBeEmpty("the route answers a bodiless 200, and a replay answers the same");
        var version = await ColumnAsync<int>("version", grant);

        var replay = await ExchangeAsync(client, HttpMethod.Post, ApproveUrl(grant), Admin(founder, org), key);

        replay.Status.ShouldBe(HttpStatusCode.OK, "a replay answers the stored decision, not a second one");
        replay.Body.ShouldBeEmpty("the stored success carried no body");
        (await ColumnAsync<int>("version", grant)).ShouldBe(version, "a replay writes nothing; the row did not move");
        (await AuditCountAsync("support_grant.approve"))
            .ShouldBe(1, "the original request already wrote the only audit row");
    }

    [Fact]
    public async Task Revoke_SameKeyReplay_AnswersStored200AndWritesNothingAgain()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "approved", approver: founder);
        var key = NewKey();

        var first = await ExchangeAsync(client, HttpMethod.Post, RevokeUrl(grant), Admin(founder, org), key);
        first.Status.ShouldBe(HttpStatusCode.OK, "the control: the key's first use revokes the grant");
        first.Body.ShouldBeEmpty("the route answers a bodiless 200, and a replay answers the same");
        var version = await ColumnAsync<int>("version", grant);

        var replay = await ExchangeAsync(client, HttpMethod.Post, RevokeUrl(grant), Admin(founder, org), key);

        replay.Status.ShouldBe(HttpStatusCode.OK, "the replay must not become the 409 a second revocation takes");
        replay.Body.ShouldBeEmpty("the stored success carried no body");
        (await ColumnAsync<int>("version", grant)).ShouldBe(version, "a replay writes nothing; the row did not move");
        (await AuditCountAsync("support_grant.revoke"))
            .ShouldBe(1, "the original request already wrote the only audit row");
    }

    [Fact]
    public async Task Approve_KeyAlreadyUsedForAnotherGrant_IsIdempotencyConflictAndChangesNothing()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var first = await SeedGrantAsync(org, "requested");
        var second = await SeedGrantAsync(org, "requested");
        var key = NewKey();

        (await ExchangeAsync(client, HttpMethod.Post, ApproveUrl(first), Admin(founder, org), key))
            .Status.ShouldBe(HttpStatusCode.OK, "the control: the key claims this grant");

        var conflict = await client.SendAsync(
            HttpMethod.Post, ApproveUrl(second), Admin(founder, org), null, key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict, conflict.ErrorMessage);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await ColumnAsync<Guid?>("approved_by", second))
            .ShouldBeNull("a reused key must not decide a grant it never claimed");
        (await AuditCountAsync("support_grant.approve")).ShouldBe(1, "only the first request wrote an audit row");
    }

    [Fact]
    public async Task Approve_WithoutIdempotencyKey_IsRefusedAndThenSucceedsWithKey()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");

        var refused = await client.SendAsync(HttpMethod.Post, ApproveUrl(grant), Admin(founder, org));

        refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        Detail(refused, "path").ShouldBe("/Idempotency-Key");
        Detail(refused, "reason").ShouldBe("required");
        (await ColumnAsync<Guid?>("approved_by", grant)).ShouldBeNull("the refusal wrote nothing");
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0, "a refused request is never audited");

        // The positive control for the same request: the only change is the key that was missing.
        (await client.SendAsync(HttpMethod.Post, ApproveUrl(grant), Admin(founder, org), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await ColumnAsync<Guid?>("approved_by", grant)).ShouldBe(founder);
    }

    [Fact]
    public async Task Revoke_WithoutIdempotencyKey_IsRefusedAndThenSucceedsWithKey()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "approved", approver: founder);

        var refused = await client.SendAsync(HttpMethod.Post, RevokeUrl(grant), Admin(founder, org));

        refused.Status.ShouldBe(HttpStatusCode.BadRequest, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        Detail(refused, "path").ShouldBe("/Idempotency-Key");
        Detail(refused, "reason").ShouldBe("required");
        (await _fixture.ScalarAsync<bool>(
                _database!, $"select revoked_at is null from public.support_grant where id = '{grant}'"))
            .ShouldBeTrue("the refusal wrote nothing");
        (await AuditCountAsync("support_grant.revoke")).ShouldBe(0, "a refused request is never audited");

        // The positive control for the same request: the only change is the key that was missing.
        (await client.SendAsync(HttpMethod.Post, RevokeUrl(grant), Admin(founder, org), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<bool>(
                _database!, $"select revoked_at is not null from public.support_grant where id = '{grant}'"))
            .ShouldBeTrue("the positive control revoked the grant");
    }

    /// <summary>
    /// Revocation is open to every holder of <c>support:approve</c>, not just a built-in admin: the person who
    /// approved may be the one who is unreachable, so a custom role carrying the permission must be enough.
    /// </summary>
    [Fact]
    public async Task Revoke_ByCustomRoleHolder_SucceedsAndAuditsThatMember()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "approved", approver: founder);
        var holder = await SeedCustomApproverAsync(org);

        var revoked = await client.SendAsync(
            HttpMethod.Post, RevokeUrl(grant), Admin(holder, org), null, NewKey());

        revoked.Status.ShouldBe(HttpStatusCode.OK, revoked.ErrorMessage);
        (await LatestAuditAsync("support_grant.revoke"))
            .ShouldBe($"user|{holder:D}|support_grant|{grant:D}|{org:D}", "the audit row names the deciding member");
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("200", 4)]
    public async Task List_InclusiveLimitBounds_AreAccepted(string limit, int expected)
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        await SeedGrantAsync(org, "requested");
        await SeedGrantAsync(org, "approved", approver: founder);
        await SeedGrantAsync(org, "revoked");
        await SeedGrantAsync(org, "expired");

        var page = await GetAsync(client, founder, org, $"?limit={limit}");

        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
        Items(page).Count.ShouldBe(expected, $"limit={limit} is inside the documented 1..200 range");
    }

    private static string ApproveUrl(Guid grant) => $"/v1/support-grants/{grant}/approve";

    private static string RevokeUrl(Guid grant) => $"/v1/support-grants/{grant}/revoke";

    /// <summary>
    /// A decision that loses the row to a concurrent writer must answer 412 and leave nothing behind. The
    /// other writer here commits a real approval while this request is inside its update, which is the only
    /// interleaving that reaches the version predicate: a second sequential approve would stop at the
    /// requested-state pre-check with 409 long before the update.
    /// </summary>
    [Fact]
    public async Task Approve_ConcurrentDecisionCommitsBetweenReadAndSave_Is412AndWritesNothing()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");
        var key = NewKey();

        // The holder is a real approval of the same row (the page's own columns and trigger), just committed
        // from another session and kept open until this request is seen waiting on it.
        await using var holder = await _fixture.OpenSuperuserAsync(_database!);
        await using var held = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var claim = new NpgsqlCommand(
            $"update public.support_grant set approved_by = '{founder}', approved_at = statement_timestamp() where id = '{grant}'",
            holder,
            held))
        {
            (await claim.ExecuteNonQueryAsync(TestContext.Current.CancellationToken))
                .ShouldBe(1, "the holder must really own the row");
        }

        var holderPid = (int)(await new NpgsqlCommand("select pg_backend_pid()", holder, held)
            .ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        var losing = client.SendAsync(HttpMethod.Post, ApproveUrl(grant), Admin(founder, org), null, key);

        await WaitBlockedByAsync(holderPid, losing);
        await held.CommitAsync(TestContext.Current.CancellationToken);

        var loser = await losing;
        loser.Status.ShouldBe(HttpStatusCode.PreconditionFailed, loser.ErrorMessage);
        loser.ErrorCode.ShouldBe("precondition_failed");
        (await ColumnAsync<int>("version", grant)).ShouldBe(2, "exactly one decision wrote the row");
        (await ColumnAsync<Guid?>("approved_by", grant)).ShouldBe(founder);
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0, "a losing decision queues no audit row");

        // The 412 rolled its idempotency row back, so the key is still usable and now meets the decided grant.
        var retry = await client.SendAsync(HttpMethod.Post, ApproveUrl(grant), Admin(founder, org), null, key);
        retry.Status.ShouldBe(HttpStatusCode.Conflict, retry.ErrorMessage);
        retry.ErrorCode.ShouldBe("grant_not_requested", "the key was usable again and met the decided grant, not a stale idempotency row");
        (await ColumnAsync<int>("version", grant)).ShouldBe(2, "the refused retry wrote nothing either");
    }

    /// <summary>
    /// Waits until the server reports some backend waiting on <paramref name="holderPid"/>. A row lock's waiter
    /// parks on the holder's transaction id, which <c>pg_locks</c> does not expose as a relation, so
    /// <c>pg_blocking_pids</c> is what resolves it. Bounded by attempts, and a request that finishes first is
    /// a failure of the test, not a pass.
    /// </summary>
    private async Task WaitBlockedByAsync(int holderPid, Task decision)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (decision.IsCompleted)
            {
                throw new InvalidOperationException(
                    "the decision completed instead of waiting on the held row, so the 412 path was not reached");
            }

            var blocked = await _fixture.ScalarAsync<bool>(
                _database!,
                $"select exists (select 1 from pg_stat_activity a where a.datname = current_database() and {holderPid} = any (pg_blocking_pids(a.pid)))");

            if (blocked)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"no backend was observed waiting on backend {holderPid}");
    }

    /// <summary>The one detail of the error envelope's first entry, addressed by its field name.</summary>
    private static string? Detail(ApiAnswer answer, string field) =>
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty(field).GetString();

    /// <summary>
    /// One request whose response body is read as sent: the replay tests assert the bodiless 200 on the real
    /// content, which the parsed <see cref="ApiAnswer"/> cannot show once it has been normalised to <c>{}</c>.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> ExchangeAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (response.StatusCode, body);
    }
}

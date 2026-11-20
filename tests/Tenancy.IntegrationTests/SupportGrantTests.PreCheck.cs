using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The approve and revoke handlers' own refusals, with the guard trigger off: with it on, the trigger refuses the
/// same cases and the handler maps that to the same answer, so no test could tell the two apart. Each test disables
/// the trigger in its own dedicated database, which is dropped when the test ends.
/// </summary>
public sealed partial class SupportGrantTests
{
    private const string TriggerState =
        "select tgenabled::text from pg_catalog.pg_trigger where tgname = 'support_grant_guard' and tgrelid = 'public.support_grant'::regclass";

    [Fact]
    public async Task Approve_TriggerOff_NonAdminHolder_IsStillRefusedByThePreCheck()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");
        var twin = await SeedGrantAsync(org, "requested");
        var approver = await SeedCustomApproverAsync(org);
        await DisableGuardAsync();

        // Control: the approver clause would refuse this statement, and with the trigger off the database takes it.
        await _fixture.ExecuteAsync(
            _database!,
            $"update public.support_grant set approved_by = '{approver}', approved_at = now() where id = '{twin}'");
        (await ColumnAsync<Guid?>("approved_by", twin)).ShouldBe(approver, "the trigger is really off");

        var before = await StateAsync(grant);
        var refused = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(approver, org), null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("approver_not_tenant_admin");
        (await StateAsync(grant)).ShouldBe(before);
        (await ColumnAsync<Guid?>("approved_by", grant)).ShouldBeNull();
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0);

        // The same request by the tenant_admin founder is accepted, so the refusal is about the approver alone.
        (await client.SendAsync(HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(founder, org), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("approved")]
    public async Task Approve_TriggerOff_GrantNotAwaitingApproval_IsStillConflict(string state)
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, state, approver: founder);
        var twin = await SeedGrantAsync(org, state, approver: founder);
        var open = await SeedGrantAsync(org, "requested");
        await DisableGuardAsync();

        // Control: the guard refuses this move on a grant of the same state; with it off the row changes.
        var twinBefore = await StateAsync(twin);
        await _fixture.ExecuteAsync(
            _database!,
            state == "approved"
                ? $"update public.support_grant set approved_at = approved_at + interval '1 second' where id = '{twin}'"
                : $"update public.support_grant set approved_by = '{founder}', approved_at = now() where id = '{twin}'");
        (await StateAsync(twin)).ShouldNotBe(twinBefore, "the trigger is really off");
        var before = await StateAsync(grant);
        var refused = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(founder, org), null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("grant_not_requested");
        (await StateAsync(grant)).ShouldBe(before);
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0);

        // Same caller, same route, a grant that is awaiting approval: accepted, so the refusal is about the state.
        (await client.SendAsync(HttpMethod.Post, $"/v1/support-grants/{open}/approve", Admin(founder, org), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_TriggerOff_AlreadyRevokedGrant_IsStillConflict()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "revoked");
        var twin = await SeedGrantAsync(org, "revoked");
        await DisableGuardAsync();

        // Control: the guard refuses un-revoking a revoked grant; with it off the row changes.
        await _fixture.ExecuteAsync(_database!, $"update public.support_grant set revoked_at = null where id = '{twin}'");
        (await ColumnAsync<DateTimeOffset?>("revoked_at", twin)).ShouldBeNull("the trigger is really off");

        var before = await StateAsync(grant);
        var refused = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/revoke", Admin(founder, org), null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("grant_already_revoked");
        (await StateAsync(grant)).ShouldBe(before);
        (await AuditCountAsync("support_grant.revoke")).ShouldBe(0);

        // Same caller, same route, the twin the control just un-revoked: accepted, so the refusal is about the state.
        (await client.SendAsync(HttpMethod.Post, $"/v1/support-grants/{twin}/revoke", Admin(founder, org), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    private async Task DisableGuardAsync()
    {
        await _fixture.ExecuteAsync(_database!, "alter table public.support_grant disable trigger support_grant_guard");
        (await _fixture.ScalarAsync<string>(_database!, TriggerState)).ShouldBe("D");
    }

    private Task<string> StateAsync(Guid grant) =>
        _fixture.ScalarAsync<string>(
            _database!,
            $"select to_jsonb(g)::text from public.support_grant g where g.id = '{grant}'");
}

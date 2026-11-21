using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class IngestDedupeTests
{
    // ------------------------------------------------------------------ §8 P2: one insert + one read-back

    [Fact]
    public async Task dedupe_write_statements_are_one_insert_plus_one_conflict_select()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        // A marker only the request body carries: it can reach a command text only by concatenation (S1). It rides
        // `ext.marker`, which the allow-list keeps on `run.log` only (EventExtAllowList.IsAllowed), so the stored
        // payload really holds it and the "never a literal" assertion below cannot pass vacuously.
        var sentinel = $"payload-literal-{first:N}";
        var body = IngestQaRequests.Batch(
            IngestQaRequests.Event(
                first, IngestQaRequests.KeyOf(world, 0), sequence: 1, type: "run.log", status: null,
                ext: new { schema = "playwright/1", marker = sentinel }),
            IngestQaRequests.Event(
                second, IngestQaRequests.KeyOf(world, 1), sequence: 2, type: "test.step", status: null));

        // An all-new batch: one event insert and no conflict read-back at all.
        world.Host.Statements.Clear();
        var allNew = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);

        allNew.Status.ShouldBe(HttpStatusCode.OK, allNew.ErrorMessage);
        Accepted(allNew).ShouldBe(2);

        var allNewStatements = world.Host.Statements.Snapshot();
        var allNewInserts = allNewStatements.Where(IsEventInsert).ToList();
        allNewInserts.Count.ShouldBe(1, string.Join(Environment.NewLine, allNewStatements));
        allNewStatements.Where(IsConflictReadBack).ShouldBeEmpty(string.Join(Environment.NewLine, allNewStatements));

        // The events array travels as one Npgsql parameter, never as a literal in the command text.
        allNewInserts[0].ShouldContain("jsonb_to_recordset(");
        allNewInserts[0].ShouldNotContain(first.ToString("D"));
        allNewStatements.ShouldAllBe(statement => !statement.Contains(sentinel, StringComparison.Ordinal));

        // Positive control: the sentinel is really in the stored row (the allow-list kept `ext.marker` on run.log),
        // so "no command text carries it" above is a parameterisation check, not a check on a dropped field.
        (await world.ScalarAsync<string>(
            $"select payload::text from public.event where event_id = '{first:D}'")).ShouldContain(sentinel);

        // A resend (every event a duplicate): still one insert, plus exactly one conflict read-back SELECT.
        world.Host.Statements.Clear();
        var resend = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);

        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        Duplicates(resend).ShouldBe(2);

        var resendStatements = world.Host.Statements.Snapshot();
        var resendInserts = resendStatements.Where(IsEventInsert).ToList();
        var resendReadBacks = resendStatements.Where(IsConflictReadBack).ToList();
        resendInserts.Count.ShouldBe(1, string.Join(Environment.NewLine, resendStatements));
        resendReadBacks.Count.ShouldBe(1, string.Join(Environment.NewLine, resendStatements));
        resendStatements.ShouldAllBe(statement => !statement.Contains(sentinel, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ cross-org and cross-run acceptance

    /// <summary>
    /// Proves exactly two things about one <c>event_id</c> under two tenants: another org's run accepts its own row
    /// with no 409, and each tenant keeps only its own row and payload — including on an all-duplicate resend, whose
    /// read-back must find that tenant's row and not the other one's. It does <em>not</em> isolate the <c>org_id</c>
    /// predicate of the read-back: the other tenant's run differs too, so a <c>run_id</c>-only filter would answer
    /// identically, and cross-org isolation rests on the composite unique key <c>(org_id, run_id, event_id)</c> and
    /// the forced RLS policy (migration 0034), not on this test.
    /// </summary>
    [Fact]
    public async Task same_event_id_in_another_org_run_is_accepted_not_conflicted()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var shared = Guid.CreateVersion7();

        var orgA = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(shared, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed")));
        orgA.Status.ShouldBe(HttpStatusCode.OK, orgA.ErrorMessage);
        Accepted(orgA).ShouldBe(1);

        // A second tenant with its own project, run and producer slot; the run is already running, so the batch has
        // no state transition to perform.
        var (otherOrg, otherProject) = await world.CreateOtherOrgAsync(Unique.Slug("globex"));
        var otherRun = await world.SeedOtherRunAsync(otherOrg);
        await RunStateWalk.MoveAsync(_fixture, world.Database, otherRun.RunId, "running");
        await IngestTestWorld.SeedSlotsAsync(_fixture, world.Database, otherOrg, otherRun.RunId, count: 1, boundJti: null);

        await using var otherHost = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), otherOrg, otherProject,
            IngestTestWorld.Writer(projectScope: otherProject));
        using var otherClient = otherHost.CreateClient();

        // The same event_id with a different payload on the other tenant's run: an ordinary accept, never a 409.
        var otherBody = IngestQaRequests.Batch(IngestQaRequests.Event(
            shared, otherRun.Executions[0].ExecutionKey, sequence: 1, status: "failed"));

        var other = await IngestQaRequests.PostEventsAsync(otherClient, otherRun.RunId, otherBody);
        other.Status.ShouldBe(HttpStatusCode.OK, other.ErrorMessage);
        other.ErrorCode.ShouldBeNull();
        Accepted(other).ShouldBe(1);
        Duplicates(other).ShouldBe(0);
        AckKinds(other).ShouldBe(["accepted"]);

        // Each tenant holds exactly its own row, with its own payload: the read-back never spans tenants.
        ((await EventRowsAsync(world)) + (await EventRowsOfOrgAsync(world, otherOrg))).ShouldBe(2);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.event where org_id = '{world.Org:D}' and event_id = '{shared:D}'")).ShouldBe(1);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.event where org_id = '{otherOrg:D}' and event_id = '{shared:D}'")).ShouldBe(1);
        (await world.ScalarAsync<string>(
            $"select raw_status from public.event where org_id = '{world.Org:D}' and event_id = '{shared:D}'"))
            .ShouldBe("passed");
        (await world.ScalarAsync<string>(
            $"select raw_status from public.event where org_id = '{otherOrg:D}' and event_id = '{shared:D}'")).ShouldBe("failed");

        // The other tenant's resend is a duplicate of its own row, not a conflict with the first tenant's.
        var resend = await IngestQaRequests.PostEventsAsync(otherClient, otherRun.RunId, otherBody);
        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        Accepted(resend).ShouldBe(0);
        Duplicates(resend).ShouldBe(1);
        AckKinds(resend).ShouldBe(["duplicate"]);
        ((await EventRowsAsync(world)) + (await EventRowsOfOrgAsync(world, otherOrg))).ShouldBe(2);
    }

    // ------------------------------------------------------------------ the run half of the dedupe key

    /// <summary>
    /// The same <c>event_id</c> in two runs of one org: both batches are accepted and each run owns one row, so the
    /// dedupe key is <c>(org_id, run_id, event_id)</c> — a cross-run resend is never a duplicate and never a 409. The
    /// two rows carry the same stored payload residue; only <c>payload_hash</c> differs, because RFC 8785
    /// canonicalises the whole envelope including each run's own <c>execution_key</c>.
    /// </summary>
    [Fact]
    public async Task same_event_id_in_two_runs_of_one_org_is_accepted_in_both()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var shared = Guid.CreateVersion7();

        // A second run of the same org (and so the same project), with its own producer slot; both runs are
        // `created`, so neither batch drives a state transition.
        var secondRun = await world.SeedOtherRunAsync(world.Org, planned: 1);
        await IngestTestWorld.SeedSlotsAsync(
            _fixture, world.Database, world.Org, secondRun.RunId, count: 1, boundJti: null);

        // One envelope shape for both runs; only the run-scoped execution key differs, because each run plans its own.
        object Body(string executionKey) => IngestQaRequests.Batch(IngestQaRequests.Event(
            shared, executionKey, sequence: 1, status: "passed"));

        var first = await IngestQaRequests.PostEventsAsync(
            world.Client, world.RunId, Body(IngestQaRequests.KeyOf(world, 0)));
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.ErrorCode.ShouldBeNull();
        Accepted(first).ShouldBe(1);
        Duplicates(first).ShouldBe(0);
        AckKinds(first).ShouldBe(["accepted"]);

        var second = await IngestQaRequests.PostEventsAsync(
            world.Client, secondRun.RunId, Body(secondRun.Executions[0].ExecutionKey));
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.ErrorCode.ShouldBeNull();
        Accepted(second).ShouldBe(1);
        Duplicates(second).ShouldBe(0);
        AckKinds(second).ShouldBe(["accepted"]);

        // One row per run: same event_id and same stored payload residue (`payload` is the ext residue; the payload
        // hashes differ only because each run's execution key is canonicalised in). No duplicate, no 409, and two
        // committed receipts.
        (await EventRowsAsync(world)).ShouldBe(2);
        (await BatchRowsAsync(world)).ShouldBe(2);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.event where run_id = '{world.RunId:D}' and event_id = '{shared:D}'")).ShouldBe(1);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.event where run_id = '{secondRun.RunId:D}' and event_id = '{shared:D}'")).ShouldBe(1);

        var firstPayload = await world.ScalarAsync<string>(
            $"select payload::text from public.event where event_id = '{shared:D}' and run_id = '{world.RunId:D}'");
        var secondPayload = await world.ScalarAsync<string>(
            $"select payload::text from public.event where event_id = '{shared:D}' and run_id = '{secondRun.RunId:D}'");
        secondPayload.ShouldBe(firstPayload);
    }
}

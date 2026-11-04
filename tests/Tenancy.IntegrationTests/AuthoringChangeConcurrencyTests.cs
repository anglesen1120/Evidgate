using System.Net;
using System.Text.Json;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The queue's database-side half, split from the route suite to keep each file under the size cap: the cases here hold a
/// real transaction open and wait for an observable <c>pg_locks</c> waiter, and the ones that do not still need the store to
/// answer (the stamped affected set, the whole-document schema, the split's case membership, the bounded project lock).
/// </summary>
public sealed partial class AuthoringChangeTests
{
    /// <summary>
    /// The overlap guard under real contention, proven rather than hoped for: an uncommitted structural insert for the same
    /// item holds the project's queue lock until the request under test is observed parked in <c>pg_locks</c>, and only
    /// then commits. So the 409 comes from the handler's pre-check seeing the committed row, not from a scheduling accident,
    /// and the project never holds two pending structural changes for that item.
    /// </summary>
    [Fact]
    public async Task Queue_OverlappingStructuralChangeParksOnTheProjectLock_ThenSees409()
    {
        await using var world = await StartAsync(2);
        await using var competitor = await OpenHolderAsync(world);
        await InsertPendingStructuralAsync(world, competitor, world.Items[0]);

        var request = QueueAsync(world, world.Items[0], "merge", MergeJson(world.Items[0], world.Items[1]));
        await AwaitProjectLockWaiterAsync(world);
        request.IsCompleted.ShouldBeFalse("the queue request parks on the project lock the open insert holds");

        await competitor.CommitAsync();

        AssertAlreadyPending(
            await request.WaitAsync(WaitLimit, TestContext.Current.CancellationToken),
            "/target_id");
        (await PendingCountAsync(world)).ShouldBe(1);
        (await ChangeCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// The same guard without the handler in the way: a writer that takes the project lock itself and then inserts a
    /// change overlapping a pending item is refused by the trigger. This is the second line of defence, so it is proven
    /// separately from the pre-check above — the insert parks on the lock, and the commit is what lets the trigger answer.
    /// </summary>
    [Fact]
    public async Task Queue_TriggerRefusesAnOverlappingInsertParkedOnTheProjectLock()
    {
        await using var world = await StartAsync(2);
        await using var holder = await OpenHolderAsync(world);
        await InsertPendingStructuralAsync(world, holder, world.Items[0]);

        await using var writer = await OpenHolderAsync(world);
        var overlapping = InsertPendingStructuralAsync(world, writer, world.Items[0]);
        await AwaitProjectLockWaiterAsync(world);
        overlapping.IsCompleted.ShouldBeFalse("the second insert parks on the project lock inside the guard trigger");

        await holder.CommitAsync();

        PostgresException? violation = null;
        try
        {
            await overlapping;
        }
        catch (PostgresException exception)
        {
            violation = exception;
        }

        violation.ShouldNotBeNull();
        violation.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        violation.MessageText.ShouldContain("authoring_change_affected");
        (await PendingCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A structural queue really binds its affected set: the pre-check's key list is a <c>text[]</c> for jsonb's
    /// <c>?|</c>, and before that parameter existed the request died inside Npgsql's array type handler instead of
    /// answering. The row it writes carries exactly the items the payload named, each at its stamped version.
    /// </summary>
    [Fact]
    public async Task Queue_StructuralChange_StampsItsAffectedSetFromTheBoundKeys()
    {
        await using var world = await StartAsync(3);

        var merge = await QueueAsync(world, world.Items[1], "merge", MergeJson(world.Items[0], world.Items[1]));
        merge.Status.ShouldBe(HttpStatusCode.Created, merge.ErrorMessage);

        var stored = await fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select affected_versions::text from public.authoring_change
             where id = '{merge.Body.GetProperty("change_id").GetGuid():D}'
            """);

        using var affected = JsonDocument.Parse(stored!);
        affected.RootElement.EnumerateObject().Select(member => member.Name)
            .ShouldBe(
                new[] { world.Items[0].ToString("D"), world.Items[1].ToString("D") },
                ignoreOrder: true);
        affected.RootElement.EnumerateObject()
            .ShouldAllBe(member => member.Value.GetInt32() >= 1);
    }

    /// <summary>
    /// The queue document is validated whole against <c>authoring-change.v1</c> before anything is stored: a member a
    /// projection would drop (an unknown root member), a member of the wrong kind (a numeric reason) or a missing one
    /// (no payload) is the schema's 422, and none of the three reaches the database or the audit trail.
    /// </summary>
    [Fact]
    public async Task Queue_RefusesADocumentTheWholeDocumentSchemaDoesNotAccept()
    {
        await using var world = await StartAsync(2);

        var unknownRoot = await QueueObjectAsync(world, new
        {
            target_kind = "checklist_item",
            target_id = world.Items[0],
            change_type = "delete",
            payload = new { },
            snapshot = new { },
        });
        AssertInvalid(unknownRoot, "/snapshot", "additional_properties");

        var numericReason = await QueueObjectAsync(world, new
        {
            target_kind = "checklist_item",
            target_id = world.Items[0],
            change_type = "delete",
            payload = new { },
            reason = 42,
        });
        AssertInvalid(numericReason, "/reason", "type");

        var missingPayload = await QueueObjectAsync(world, new
        {
            target_kind = "checklist_item",
            target_id = world.Items[0],
            change_type = "delete",
        });
        missingPayload.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, missingPayload.ErrorMessage);
        missingPayload.ErrorCode.ShouldBe("request_invalid");
        missingPayload.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("/payload");

        (await ChangeCountAsync(world)).ShouldBe(0);
        (await AuditCountAsync(world, "authoring.change.queue")).ShouldBe(0);
    }

    /// <summary>
    /// A split's assignment may only name a live case of the item being split: the case of another item is refused at the
    /// index the caller wrote it at, and the same shape aimed at the right item is accepted, so the refusal is the
    /// membership rule rather than the payload shape.
    /// </summary>
    [Fact]
    public async Task Queue_SplitAssignmentOfACaseOutsideTheTarget_Is422AtIndex()
    {
        await using var world = await StartAsync(2);
        var ownCase = await AuthoringLinkTestData.SeedManualCaseAsync(
            fixture,
            world.Database,
            new AuthoringLinkTree(world.Org, world.Project, world.Items[0]));

        var accepted = await QueueAsync(world, world.Items[0], "split", SplitWithAssignment(ownCase));
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);

        var foreign = await QueueAsync(world, world.Items[1], "split", SplitWithAssignment(ownCase));
        AssertInvalid(foreign, "/payload/case_assignments/0/case_id", "payload_invalid");
        (await ChangeCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// The project's queue lock is taken under a bound, so a writer that holds it past that bound parks the request
    /// instead of piling up: the answer is the retryable 503 the apply route gives, and nothing is written. The holder
    /// is released before the test ends so a failing assertion cannot leave the project locked for the next case.
    /// </summary>
    [Fact]
    public async Task Queue_WhenTheProjectLockIsHeldPastTheBound_Is503Retryable()
    {
        await using var world = await StartAsync(1);
        await using var holder = await OpenHolderAsync(world);
        await LockProjectAsync(world, holder);

        var answer = await QueueAsync(world, world.Items[0], "comment", """{"body":"parked"}""")
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await holder.CommitAsync();

        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("unavailable");
        answer.Body.GetProperty("error").GetProperty("retryable").GetBoolean().ShouldBeTrue();
        (await ChangeCountAsync(world)).ShouldBe(0);
    }
}

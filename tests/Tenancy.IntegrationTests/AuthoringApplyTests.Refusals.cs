using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The refusals of the batched apply: a selection that cannot land wholly writes nothing, and an id of another project
/// or tenant is the same 404 as a random one.
/// </summary>
public sealed partial class AuthoringApplyTests
{
    /// <summary>AC13: the apply-time re-check refuses a delete whose target gained a live manual case.</summary>
    [Fact]
    public async Task Apply_DeleteTargetWithLiveCase_409_AndNothingWritten()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        await InsertCaseAsync(world, item);
        var before = await CountVersionsAsync(world, item);
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var refused = await ApplyAsync(world, [change], BatchEtag((item, version)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("authoring_not_empty");
        Reasons(refused).ShouldContain("item_has_cases");
        (await CountVersionsAsync(world, item)).ShouldBe(before);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is null", ("id", item))).ShouldBe(1);
        (await CountAsync(world, "select count(*) from public.manual_test_case where checklist_item_id = @id and deleted_at is null and version = 1", ("id", item))).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore);
    }

    /// <summary>
    /// AC8/main decision: a selection holding a structural change this route does not execute is refused fail-closed —
    /// the whole batch, nothing written, and the row stays <c>pending</c> rather than being marked applied.
    /// </summary>
    [Fact]
    public async Task Apply_MergeSelection_RefusedAndStaysPending()
    {
        await using var world = await StartAsync();
        var (survivor, survivorVersion) = await CreateItemAsync(world, "Pay online");
        var (source, sourceVersion) = await CreateItemAsync(world, "Pay online by card");
        var change = await QueueMergeAsync(world, survivor, source);
        var etag = BatchEtag((survivor, survivorVersion), (source, sourceVersion));

        var refused = await ApplyAsync(world, [change], etag);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(refused).ShouldContain("payload_invalid");
        Paths(refused).ShouldContain("/change_ids/0");
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
        (await CountVersionsAsync(world, survivor)).ShouldBe(1);
        (await CountVersionsAsync(world, source)).ShouldBe(1);
    }

    /// <summary>The split half of AC8: the same fail-closed refusal a merge selection gets, with nothing written.</summary>
    [Fact]
    public async Task Apply_SplitSelection_RefusedAndStaysPending()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueSplitAsync(world, item);

        var refused = await ApplyAsync(world, [change], BatchEtag((item, version)));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Reasons(refused).ShouldContain("payload_invalid");
        Paths(refused).ShouldContain("/change_ids/0");
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
        (await CountVersionsAsync(world, item)).ShouldBe(1);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is null", ("id", item))).ShouldBe(1);
    }

    /// <summary>
    /// AC8: one stale target refuses the whole selection — the valid change of the same batch is not applied, writes no
    /// version, flips no state and appends no audit row.
    /// </summary>
    [Fact]
    public async Task Apply_MixedSelection_StaleTarget_RefusesWholeBatch()
    {
        await using var world = await StartAsync();
        var (valid, validVersion) = await CreateItemAsync(world, "Pay online");
        var (moved, movedVersion) = await CreateItemAsync(world, "Pay by card");
        var validChange = await QueueDeleteAsync(world, valid);
        var movedChange = await QueueDeleteAsync(world, moved);

        var edited = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{moved:D}",
            world.Admin,
            new { title = "Pay by card online" },
            NewKey(),
            $"\"{movedVersion}\"");
        edited.Status.ShouldBe(HttpStatusCode.OK);
        var movedNow = edited.Body.GetProperty("version").GetInt32();
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var refused = await ApplyAsync(
            world,
            [validChange, movedChange],
            BatchEtag((valid, validVersion), (moved, movedNow)));

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        Reasons(refused).ShouldContain("target_changed");
        Paths(refused).ShouldContain(moved.ToString("D"));

        // The valid change did not land either: still pending, its item live at its own version, no version row.
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", validChange))).ShouldBe("pending");
        (await IntAsync(world, "select version from public.checklist_item where id = @id", ("id", valid))).ShouldBe(validVersion);
        (await CountVersionsAsync(world, valid)).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore);
    }

    /// <summary>
    /// AC13 and AC8: a live case created after queueing refuses the whole selection, including its valid sibling, and
    /// leaves the case row itself untouched.
    /// </summary>
    [Fact]
    public async Task Apply_MixedSelection_LiveCaseAfterQueue_409_AndNothingWritten()
    {
        await using var world = await StartAsync();
        var (valid, validVersion) = await CreateItemAsync(world, "Pay online");
        var (occupied, occupiedVersion) = await CreateItemAsync(world, "Pay by card");
        var validChange = await QueueDeleteAsync(world, valid);
        var occupiedChange = await QueueDeleteAsync(world, occupied);
        await InsertCaseAsync(world, occupied);
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var refused = await ApplyAsync(
            world,
            [validChange, occupiedChange],
            BatchEtag((valid, validVersion), (occupied, occupiedVersion)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("authoring_not_empty");
        Reasons(refused).ShouldContain("item_has_cases");
        Paths(refused).ShouldContain(occupied.ToString("D"));

        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", validChange))).ShouldBe("pending");
        (await CountAsync(world, "select count(*) from public.checklist_item where id = any(@ids) and deleted_at is null", ("ids", new[] { valid, occupied }))).ShouldBe(2);
        (await CountVersionsAsync(world, valid)).ShouldBe(1);
        (await CountAsync(
            world,
            "select count(*) from public.manual_test_case where checklist_item_id = @id and deleted_at is null and version = 1",
            ("id", occupied))).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore);
    }

    /// <summary>
    /// AC14: a merge's source is part of the affected set, so a <c>PATCH</c> on it after queueing answers
    /// <c>target_changed</c> naming that source. Without the edit the same selection answers the change's own 422, which
    /// is the positive control that the 412 came from the moved item and not from the merge shape.
    /// </summary>
    [Fact]
    public async Task Apply_MergeSourcePatched_412_TargetChanged()
    {
        await using var world = await StartAsync();
        var (survivor, survivorVersion) = await CreateItemAsync(world, "Pay online");
        var (source, sourceVersion) = await CreateItemAsync(world, "Pay online by card");
        var change = await QueueMergeAsync(world, survivor, source);

        var control = await ApplyAsync(world, [change], BatchEtag((survivor, survivorVersion), (source, sourceVersion)));
        control.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var edited = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{source:D}",
            world.Admin,
            new { title = "Pay online by credit card" },
            NewKey(),
            $"\"{sourceVersion}\"");
        edited.Status.ShouldBe(HttpStatusCode.OK);
        var sourceNow = edited.Body.GetProperty("version").GetInt32();

        var refused = await ApplyAsync(world, [change], BatchEtag((survivor, survivorVersion), (source, sourceNow)));

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        Reasons(refused).ShouldContain("target_changed");
        Paths(refused).ShouldContain(source.ToString("D"));
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
        (await CountAsync(world, "select count(*) from public.checklist_item where id = any(@ids) and deleted_at is null", ("ids", new[] { survivor, source }))).ShouldBe(2);
    }

    /// <summary>
    /// AC8: a target deleted after its change was queued answers <c>target_deleted</c> for the whole batch, even when the
    /// caller recomputed the tag from the row's current version.
    /// </summary>
    [Fact]
    public async Task Apply_DeletedTargetAfterQueue_412_TargetDeleted()
    {
        await using var world = await StartAsync();
        var (item, _) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var (other, otherVersion) = await CreateItemAsync(world, "Pay by card");
        var otherChange = await QueueDeleteAsync(world, other);

        await ExecuteAsync(
            world,
            "update public.checklist_item set deleted_at = clock_timestamp() where id = @id",
            ("id", item));
        var itemNow = await IntAsync(world, "select version from public.checklist_item where id = @id", ("id", item));

        var refused = await ApplyAsync(world, [otherChange, change], BatchEtag((other, otherVersion), (item, itemNow)));

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        Reasons(refused).ShouldContain("target_deleted");
        Paths(refused).ShouldContain(item.ToString("D"));
        // The unaffected change of the same selection did not land.
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", otherChange))).ShouldBe("pending");
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is null", ("id", other))).ShouldBe(1);
        (await CountVersionsAsync(world, other)).ShouldBe(1);
    }

    /// <summary>AC11: 201 ids are refused by shape, and 200 are accepted by it (they then 404 as unknown ids).</summary>
    [Fact]
    public async Task Apply_SelectionOverLimit_422_AndTwoHundredIdsAccepted()
    {
        await using var world = await StartAsync();
        var tooMany = Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()).ToArray();
        var atLimit = Enumerable.Range(0, 200).Select(_ => Guid.CreateVersion7()).ToArray();

        var refused = await ApplyAsync(world, tooMany, BatchEtag((tooMany[0], 1)));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Reasons(refused).ShouldContain("too_many_changes");
        Paths(refused).ShouldContain("/change_ids");
        (await CountAsync(world, "select count(*) from public.authoring_change where org_id = @org and state <> 'pending'", ("org", world.Org))).ShouldBe(0);

        // The shape accepts exactly 200: the answer is the lock's own 404 for ids this project cannot see.
        var accepted = await ApplyAsync(world, atLimit, BatchEtag((atLimit[0], 1)));
        accepted.Status.ShouldBe(HttpStatusCode.NotFound);
        accepted.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// AC12: a change of another project or another tenant is the same 404 an unknown id gets, and the row stays
    /// pending; the same call on the caller's own project is the positive control.
    /// </summary>
    [Fact]
    public async Task Apply_ForeignProjectOrTenantChangeId_404_AndRowStaysPending()
    {
        await using var world = await StartAsync();
        await using var other = await StartAsync();

        var (mine, mineVersion) = await CreateItemAsync(world, "Pay online");
        var mineChange = await QueueDeleteAsync(world, mine);
        var (theirs, _) = await CreateItemAsync(other, "Their item");
        var theirsChange = await QueueDeleteAsync(other, theirs);

        var unknownTag = "\"order:sha256:" + new string('0', 64) + "\"";
        var foreignTenant = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{world.Changes}:apply",
            world.Admin,
            new { change_ids = new[] { theirsChange } },
            NewKey(),
            unknownTag);
        foreignTenant.Status.ShouldBe(HttpStatusCode.NotFound);
        foreignTenant.ErrorCode.ShouldBe("not_found");
        (await TextAsync(other, "select state from public.authoring_change where id = @id", ("id", theirsChange))).ShouldBe("pending");

        // Another project of the same organization answers the same way for a change that is not the caller's there.
        var sibling = Guid.CreateVersion7();
        await ExecuteAsync(
            world,
            "insert into public.project (id, org_id, name, slug) values (@id, @org, 'Sibling', @slug)",
            ("id", sibling),
            ("org", world.Org),
            ("slug", "sib-" + sibling.ToString("N")));
        var (second, _) = await CreateItemAsync(world, "Pay by card");
        var secondChange = await QueueDeleteAsync(world, second);

        var foreignProject = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{sibling:D}/authoring/changes:apply",
            world.Admin,
            new { change_ids = new[] { secondChange } },
            NewKey(),
            unknownTag);
        foreignProject.Status.ShouldBe(HttpStatusCode.NotFound);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", secondChange))).ShouldBe("pending");

        // Positive control: the same selection on its own project applies.
        var applied = await ApplyAsync(world, [mineChange], BatchEtag((mine, mineVersion)));
        applied.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The discard half of AC12: another project's or tenant's change id is a 404 and the row keeps its state.</summary>
    [Fact]
    public async Task Discard_ForeignProjectOrTenantChangeId_404_AndRowStaysPending()
    {
        await using var world = await StartAsync();
        await using var other = await StartAsync();

        var (mine, _) = await CreateItemAsync(world, "Pay online");
        var mineChange = await QueueDeleteAsync(world, mine);
        var (theirs, _) = await CreateItemAsync(other, "Their item");
        var theirsChange = await QueueDeleteAsync(other, theirs);

        var foreignTenant = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{world.Changes}/{theirsChange:D}/discard",
            world.Admin,
            null,
            NewKey());
        foreignTenant.Status.ShouldBe(HttpStatusCode.NotFound);
        foreignTenant.ErrorCode.ShouldBe("not_found");
        (await TextAsync(other, "select state from public.authoring_change where id = @id", ("id", theirsChange))).ShouldBe("pending");

        var sibling = Guid.CreateVersion7();
        await ExecuteAsync(
            world,
            "insert into public.project (id, org_id, name, slug) values (@id, @org, 'Sibling', @slug)",
            ("id", sibling),
            ("org", world.Org),
            ("slug", "sib-" + sibling.ToString("N")));
        var foreignProject = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{sibling:D}/authoring/changes/{mineChange:D}/discard",
            world.Admin,
            null,
            NewKey());
        foreignProject.Status.ShouldBe(HttpStatusCode.NotFound);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", mineChange))).ShouldBe("pending");

        // Positive control: the caller's own project discards it.
        var discarded = await DiscardAsync(world, mineChange);
        discarded.Status.ShouldBe(HttpStatusCode.OK);
        discarded.Text("state").ShouldBe("discarded");
    }
}

using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The writes of the batched apply and its replay: one version per changed entity, nothing for a comment, and a
/// selection that lands wholly or not at all.
/// </summary>
public sealed partial class AuthoringApplyTests
{
    /// <summary>AC4 and AC8: a delete lands one version, one batch, and flips the change to applied.</summary>
    [Fact]
    public async Task Apply_Delete_WritesOneVersionAndFlipsTheChange()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var before = await CountVersionsAsync(world, item);

        var applied = await ApplyAsync(world, [change], BatchEtag((item, version)));

        applied.Status.ShouldBe(HttpStatusCode.OK);
        var batch = applied.Body.GetProperty("batch_id").GetGuid();
        applied.Body.GetProperty("applied_change_ids")[0].GetGuid().ShouldBe(change);
        var versions = applied.Body.GetProperty("versions");
        versions.GetArrayLength().ShouldBe(1);
        versions[0].GetProperty("entity_id").GetGuid().ShouldBe(item);
        versions[0].GetProperty("version_no").GetInt32().ShouldBe(version + 1);
        applied.Body.GetProperty("lineage").GetArrayLength().ShouldBe(0);

        // Exactly one version more than before, all of them under this one batch, and the row moved once.
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
        (await CountAsync(world, "select count(*) from public.authoring_version where batch_id = @batch", ("batch", batch))).ShouldBe(1);
        (await CountAsync(world, "select count(*) from public.authoring_change where id = @id and state = 'applied' and applied_batch_id = @batch", ("id", change), ("batch", batch))).ShouldBe(1);
        // The delete is a soft delete: the row is retained with deleted_at set, never physically removed.
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is not null", ("id", item))).ShouldBe(1);
    }

    /// <summary>AC4: a comment-only batch versions nothing, so no editor's <c>If-Match</c> is invalidated by it.</summary>
    [Fact]
    public async Task Apply_CommentOnly_WritesNoVersion()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueCommentAsync(world, item, "please rename this item");
        var before = await CountVersionsAsync(world, item);

        var applied = await ApplyAsync(world, [change], BatchEtag((item, version)));

        applied.Status.ShouldBe(HttpStatusCode.OK);
        applied.Body.GetProperty("versions").GetArrayLength().ShouldBe(0);
        (await CountVersionsAsync(world, item)).ShouldBe(before);
        (await IntAsync(world, "select version from public.checklist_item where id = @id", ("id", item))).ShouldBe(version);
        (await CountAsync(world, "select count(*) from public.authoring_change where id = @id and state = 'applied'", ("id", change))).ShouldBe(1);
    }

    /// <summary>AC4: a batch of two deletes is one version with exactly one row per changed entity.</summary>
    [Fact]
    public async Task Apply_MultipleEntities_OneBatchAndOneRowPerEntity()
    {
        await using var world = await StartAsync();
        var (first, firstVersion) = await CreateItemAsync(world, "Pay online");
        var (second, secondVersion) = await CreateItemAsync(world, "Pay by card");
        var firstChange = await QueueDeleteAsync(world, first);
        var secondChange = await QueueDeleteAsync(world, second);
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var applied = await ApplyAsync(world, [firstChange, secondChange], BatchEtag((first, firstVersion), (second, secondVersion)));

        applied.Status.ShouldBe(HttpStatusCode.OK);
        var batch = applied.Body.GetProperty("batch_id").GetGuid();
        applied.Body.GetProperty("applied_change_ids").GetArrayLength().ShouldBe(2);
        applied.Body.GetProperty("versions").GetArrayLength().ShouldBe(2);

        // One batch, one version row per entity, each at the item's next version.
        (await CountAsync(world, "select count(*) from public.authoring_version where batch_id = @batch", ("batch", batch))).ShouldBe(2);
        (await IntAsync(
            world,
            "select version_no from public.authoring_version where batch_id = @batch and entity_id = @item",
            ("batch", batch),
            ("item", second))).ShouldBe(secondVersion + 1);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = any(@ids) and deleted_at is not null", ("ids", new[] { first, second }))).ShouldBe(2);
        // The command, not the batch's rows, is what the audit records.
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore + 1);
    }

    /// <summary>AC4/AC14: the response and the writes follow the caller's order, not the id order of the locks.</summary>
    [Fact]
    public async Task Apply_ReversedInputOrder_OneBatchInTheCallersOrder()
    {
        await using var world = await StartAsync();
        var (first, firstVersion) = await CreateItemAsync(world, "Pay online");
        var (second, secondVersion) = await CreateItemAsync(world, "Pay by card");
        var firstChange = await QueueDeleteAsync(world, first);
        var secondChange = await QueueDeleteAsync(world, second);

        var applied = await ApplyAsync(world, [secondChange, firstChange], BatchEtag((first, firstVersion), (second, secondVersion)));

        applied.Status.ShouldBe(HttpStatusCode.OK);
        var ids = applied.Body.GetProperty("applied_change_ids").EnumerateArray().Select(element => element.GetGuid()).ToArray();
        ids.ShouldBe([secondChange, firstChange]);
        applied.Body.GetProperty("versions").GetArrayLength().ShouldBe(2);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = any(@ids) and deleted_at is not null", ("ids", new[] { first, second }))).ShouldBe(2);
    }

    /// <summary>AC5: a tag that is not the current batch ETag is refused before the first write.</summary>
    [Fact]
    public async Task Apply_StaleIfMatch_RefusesAndWritesNothing()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var before = await CountVersionsAsync(world, item);
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var refused = await ApplyAsync(world, [change], "0f0e0d0c0b0a09080706050403020100");

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        refused.ErrorCode.ShouldBe("precondition_failed");
        Reasons(refused).ShouldContain("batch_stale");
        Paths(refused).ShouldContain("/If-Match");

        (await CountVersionsAsync(world, item)).ShouldBe(before);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
        (await IntAsync(world, "select version from public.checklist_item where id = @id", ("id", item))).ShouldBe(version);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore);
    }

    /// <summary>
    /// AC5 and AC14(b): a <c>PATCH</c> on an affected item after queueing is refused even when the caller's tag was
    /// recomputed from the current versions, and the 412 names that item rather than the header.
    /// </summary>
    [Fact]
    public async Task Apply_ItemMovedAfterQueue_412_TargetChanged()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);

        var edited = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{item:D}",
            world.Admin,
            new { title = "Pay online with a card" },
            NewKey(),
            "\"1\"");
        edited.Status.ShouldBe(HttpStatusCode.OK);
        var moved = edited.Body.GetProperty("version").GetInt32();
        moved.ShouldBe(version + 1);
        var afterEdit = await CountVersionsAsync(world, item);

        var refused = await ApplyAsync(world, [change], BatchEtag((item, moved)));

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        Reasons(refused).ShouldContain("target_changed");
        Paths(refused).ShouldContain(item.ToString("D"));
        // The create and the edit wrote one version each and the refused apply wrote none.
        (await CountVersionsAsync(world, item)).ShouldBe(afterEdit);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
    }

    /// <summary>AC6: the same key replays the first result instead of applying a second time.</summary>
    [Fact]
    public async Task Apply_Replay_AnswersTheFirstResult()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var key = NewKey();
        var etag = BatchEtag((item, version));
        var before = await CountVersionsAsync(world, item);
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var first = await ApplyAsync(world, [change], etag, key);
        var replay = await ApplyAsync(world, [change], etag, key);

        first.Status.ShouldBe(HttpStatusCode.OK);
        replay.Status.ShouldBe(HttpStatusCode.OK);
        replay.Body.GetProperty("batch_id").GetGuid().ShouldBe(first.Body.GetProperty("batch_id").GetGuid());
        // The replay is the stored response itself, not a second apply's answer.
        replay.Body.GetRawText().ShouldBe(first.Body.GetRawText());
        // The replay writes nothing: one version more than the create's, not two, and one audit row, not two.
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore + 1);
    }

    /// <summary>
    /// AC6: a reused key whose selection differs is the documented 409, never the earlier result and never a second
    /// apply: the second change stays pending, no version is written and no audit row is appended.
    /// </summary>
    [Fact]
    public async Task Apply_SameKeyDifferentSelection_RefusedWithIdempotencyConflict()
    {
        await using var world = await StartAsync();
        var (first, firstVersion) = await CreateItemAsync(world, "Pay online");
        var (second, secondVersion) = await CreateItemAsync(world, "Pay by card");
        var firstChange = await QueueDeleteAsync(world, first);
        var secondChange = await QueueDeleteAsync(world, second);
        var key = NewKey();

        var applied = await ApplyAsync(world, [firstChange], BatchEtag((first, firstVersion)), key);
        var replayed = await ApplyAsync(world, [firstChange], BatchEtag((first, firstVersion)), key);
        applied.Status.ShouldBe(HttpStatusCode.OK);
        replayed.Status.ShouldBe(HttpStatusCode.OK);
        replayed.Body.GetRawText().ShouldBe(applied.Body.GetRawText());

        var beforeSecond = await CountVersionsAsync(world, second);
        var auditBefore = await AuditCountAsync(world, "authoring.change.apply");

        var refused = await ApplyAsync(world, [secondChange], BatchEtag((second, secondVersion)), key);

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("idempotency_conflict");
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", secondChange))).ShouldBe("pending");
        (await CountVersionsAsync(world, second)).ShouldBe(beforeSecond);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is null", ("id", second))).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.change.apply")).ShouldBe(auditBefore);
    }

    /// <summary>AC7: two appliers of one selection serialize on the locked rows; exactly one lands, neither deadlocks.</summary>
    [Fact]
    public async Task Apply_Concurrent_SameSelection_ExactlyOneLands()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var etag = BatchEtag((item, version));
        var before = await CountVersionsAsync(world, item);

        var both = await Task.WhenAll(
            ApplyAsync(world, [change], etag),
            ApplyAsync(world, [change], etag));

        both.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        var refused = both.Single(answer => answer.Status != HttpStatusCode.OK);

        // A refusal, never a 40P01: the loser blocked on the change row and read the committed `applied` state.
        refused.Status.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
        refused.ErrorMessage.ShouldNotBeNull();
        // The winner wrote the batch's one version and the loser wrote nothing.
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
    }

    /// <summary>
    /// AC7: the overlap is observable, not assumed. A transaction holds the selected change row, the apply blocks on
    /// it (seen in <c>pg_blocking_pids</c>), and once the holder commits the apply lands on the committed row.
    /// </summary>
    [Fact]
    public async Task Apply_Concurrent_WaitsOnTheLockedChangeRow_ThenCompletes()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var etag = BatchEtag((item, version));
        var before = await CountVersionsAsync(world, item);

        await using var holder = await TenantScope.BeginAsync(world.OwnerConnectionString);
        var holderPid = await holder.ScalarAsync<int>("select pg_backend_pid()");
        await holder.ScalarAsync<Guid>(
            "select id from public.authoring_change where id = @id for update",
            ("id", change));

        var apply = ApplyAsync(world, [change], etag);
        await WaitForChangeRowWaiterAsync(world, holderPid);

        // Blocked on the row, not failed: the lock the test holds is the one the handler waits for.
        apply.IsCompleted.ShouldBeFalse();
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");

        await holder.CommitAsync();
        var applied = await apply;

        applied.Status.ShouldBe(HttpStatusCode.OK);
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("applied");
    }

    /// <summary>AC7: two applies whose affected items do not overlap both commit, each under its own batch.</summary>
    [Fact]
    public async Task Apply_Concurrent_DisjointSelections_BothCommit()
    {
        await using var world = await StartAsync();
        var (first, firstVersion) = await CreateItemAsync(world, "Pay online");
        var (second, secondVersion) = await CreateItemAsync(world, "Pay by card");
        var firstChange = await QueueDeleteAsync(world, first);
        var secondChange = await QueueDeleteAsync(world, second);

        var both = await Task.WhenAll(
            ApplyAsync(world, [firstChange], BatchEtag((first, firstVersion))),
            ApplyAsync(world, [secondChange], BatchEtag((second, secondVersion))));

        both.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        var batches = both.Select(answer => answer.Body.GetProperty("batch_id").GetGuid()).Distinct().ToArray();
        batches.Length.ShouldBe(2);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = any(@ids) and deleted_at is not null", ("ids", new[] { first, second }))).ShouldBe(2);
        (await CountAsync(world, "select count(*) from public.authoring_version where batch_id = any(@batches)", ("batches", batches))).ShouldBe(2);
    }
}

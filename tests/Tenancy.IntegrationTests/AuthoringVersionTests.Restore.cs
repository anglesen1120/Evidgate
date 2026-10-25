using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T3.2 routes 6, 11, 18 and 24 through the real host and a real PostgreSQL: AC8's restore of a soft-deleted entity
/// and of an arbitrary version, the <c>max + 1</c> re-append when a live sibling holds the old position, the parent rule,
/// the 404 of an unknown or foreign entity and version, AC19's <c>If-Match</c> answered atomically with the write, the
/// sibling-group advisory lock (hand-over (a), OI-13-128), and AC18's one audit row per effective mutation.
/// </summary>
/// <remarks>
/// <para>
/// The writer-level halves of AC8 live in <c>AuthoringVersionTests.cs</c> (<c>Restore_ReappendsSnapshot_AtMaxPlusOne</c>,
/// <c>Restore_ItemRecordsActiveState_AndAcceptsTheNextEdit</c>); these tests pin the HTTP contract of §5 instead —
/// status, <c>{version}</c>, the error envelope — and read the rows the database stores, because the properties under test
/// (a cleared <c>deleted_at</c>, a re-appended position, an untouched history) are not all on the wire.
/// </para>
/// <para>
/// Every row is produced through a route: a seeded tombstone would test a state the product cannot reach, and the
/// version arithmetic of AC8 only holds for rows the writer created.
/// </para>
/// </remarks>
public sealed partial class AuthoringVersionTests
{
    /// <summary>The API host, one client and the ids the restore routes need, per test.</summary>
    /// <param name="Api">The host, whose statement log proves the lock of hand-over (a) is taken.</param>
    /// <param name="Client">The caller.</param>
    /// <param name="Database">The migrated database of this test.</param>
    /// <param name="User">The founder, whose <c>tenant_admin</c> membership holds <c>authoring:write</c>.</param>
    /// <param name="Org">The organization.</param>
    /// <param name="Project">The project.</param>
    private sealed record RestoreWorld(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        /// <summary>The founder's headers.</summary>
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary><c>POST /nodes</c> of 13.1.1 §5 route 2.</summary>
        public string Nodes => $"/v1/projects/{Project}/authoring/nodes";

        /// <summary><c>POST /checklist-items</c> of route 8.</summary>
        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

        /// <summary><c>POST /manual-cases</c> of route 13.</summary>
        public string Cases => $"/v1/projects/{Project}/authoring/manual-cases";

        /// <summary>One node (routes 3, 5 and 6).</summary>
        /// <param name="node">The node.</param>
        public string Node(Guid node) => $"{Nodes}/{node:D}";

        /// <summary>One checklist item (routes 9, 10 and 11).</summary>
        /// <param name="item">The item.</param>
        public string Item(Guid item) => $"{Items}/{item:D}";

        /// <summary>One manual test case (routes 15, 16 and 18).</summary>
        /// <param name="caseId">The case.</param>
        public string Case(Guid caseId) => $"{Cases}/{caseId:D}";

        /// <summary>Route 6, 11 or 18: the restore of the entity at <paramref name="url"/>.</summary>
        /// <param name="url">The entity's own url.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Read as world.Restore(url) next to the other route builders of this fixture.")]
        public string Restore(string url) => $"{url}/restore";

        /// <summary>Route 24.</summary>
        /// <param name="versionId">The <c>authoring_version</c> id.</param>
        public string VersionRestore(Guid versionId) => $"/v1/projects/{Project}/authoring/versions/{versionId:D}/restore";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>AC8, route by route: the four restores append a version, clear the tombstone and answer the new number.</summary>
    /// <param name="scenario">Which route the case drives.</param>
    [Theory]
    [InlineData("node")]
    [InlineData("item")]
    [InlineData("case")]
    [InlineData("version")]
    public async Task Restore_AppendsNewVersion(string scenario)
    {
        await using var world = await StartRestoreAsync();

        // The ladder of AC1, then one target per kind: a leaf node, an item with no case under it (or its delete is the
        // 409 of AC7), an item holding two cases, and the two cases themselves for the entity route and for route 24.
        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");
        var category = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Cards");

        var leaf = await CreateNodeAsync(world, parent: category, kind: "sub_category", title: "Refunds");
        var plainItem = await CreateItemAsync(world, category, "Pay online");
        var itemWithCases = await CreateItemAsync(world, category, "Pay by card");
        var firstCase = await CreateCaseAsync(world, itemWithCases, "Card is charged");
        var secondCase = await CreateCaseAsync(world, itemWithCases, "Card is declined");

        var (url, entityId, kind, state) = scenario switch
        {
            "node" => (world.Node(leaf), leaf, "feature_node", (string?)null),
            "item" => (world.Item(plainItem), plainItem, "checklist_item", "active"),
            "case" => (world.Case(firstCase), firstCase, "manual_test_case", "draft"),
            _ => (world.Case(secondCase), secondCase, "manual_test_case", "draft"),
        };

        // A soft delete leaves the row in place with version = 2 (create = 1, delete = 2), which is the tag AC19 lets the
        // caller present to the restore.
        (await DeleteAsync(world, url, version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var restoreUrl = scenario == "version" ? world.VersionRestore(await VersionIdAsync(world, entityId, versionNo: 1)) : world.Restore(url);

        // Route 24 takes no If-Match; the other three present the tombstone's version.
        var restored = scenario == "version"
            ? await SendAsync(world, restoreUrl, ifMatch: null)
            : await SendAsync(world, restoreUrl, ifMatch: "\"2\"");

        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        restored.Body.GetProperty("version").GetInt32().ShouldBe(3, "a restore appends version = previous + 1 (AC8)");

        // The three rows of the entity: create, delete, restore — one per change, none rewritten.
        (await RestoreVersionCountAsync(world, kind, entityId)).ShouldBe(3);
        (await LastVersionAsync(world, entityId)).ShouldBe("restore|3", "the newest row is the restore of AC8");

        // The snapshot re-appended is the last live one, byte for byte: for the entity routes that is the tombstone's
        // content, for route 24 the content of the version the caller named.
        var sourceVersion = scenario == "version" ? 1 : 2;
        (await VersionContentAsync(world, entityId, versionNo: 3))
            .ShouldBe(await VersionContentAsync(world, entityId, sourceVersion));

        // deleted_at cleared and version 3; — the position was free in its group — the old position kept, for the kinds
        // that have one (a manual test case is not positioned, 0039:287-303).
        (await EntityStateAsync(world, kind, entityId))
            .ShouldBe(kind == "manual_test_case" ? "true|3" : "true|0|3");

        if (state is not null)
        {
            (await _fixture.ScalarAsync<string>(
                world.Database,
                $"select state from public.{kind} where id = '{entityId:D}'"))
                .ShouldBe(state, "an item comes back active and a case back in draft (AC8)");
        }
    }

    /// <summary>
    /// AC8's parent rule: a node under a soft-deleted node, an item under a soft-deleted node and a case under a soft-deleted
    /// item are all refused with <c>422 authoring_parent_invalid</c> and <c>details[].reason = parent_deleted</c>, and
    /// nothing is written — the parent is restored first, then the child.
    /// </summary>
    [Fact]
    public async Task Restore_ChildUnderDeletedParent_422()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");

        // A node under a deleted node: the child is tombstoned first, then the now-empty parent.
        var child = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Cards");
        (await DeleteAsync(world, world.Node(child), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(feature), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var orphanNode = await SendAsync(world, world.Restore(world.Node(child)), ifMatch: "\"2\"");
        orphanNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        orphanNode.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(orphanNode).ShouldContain("parent_deleted");

        // Nothing written: the child is still a tombstone at the version its delete left.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select (deleted_at is not null)::text || '|' || version::text from public.feature_node where id = '{child:D}'"))
            .ShouldBe("true|2");
        (await RestoreVersionCountAsync(world, "feature_node", child)).ShouldBe(2);

        // An item under a deleted node: the same shape one rank down.
        var parentNode = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Refunds");
        var holder = await CreateNodeAsync(world, parent: parentNode, kind: "category", title: "Refund reasons");
        var item = await CreateItemAsync(world, holder, "Refund is visible");
        (await DeleteAsync(world, world.Item(item), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(holder), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var orphanItem = await SendAsync(world, world.Restore(world.Item(item)), ifMatch: "\"2\"");
        orphanItem.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        orphanItem.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(orphanItem).ShouldContain("parent_deleted");
        (await RestoreVersionCountAsync(world, "checklist_item", item)).ShouldBe(2);

        // A case under a deleted item.
        var liveNode = await CreateNodeAsync(world, parent: parentNode, kind: "category", title: "Chargebacks");
        var liveItem = await CreateItemAsync(world, liveNode, "Chargeback is filed");
        var manualCase = await CreateCaseAsync(world, liveItem, "File the chargeback");
        (await DeleteAsync(world, world.Case(manualCase), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Item(liveItem), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var orphanCase = await SendAsync(world, world.Restore(world.Case(manualCase)), ifMatch: "\"2\"");
        orphanCase.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        orphanCase.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(orphanCase).ShouldContain("parent_deleted");
        (await RestoreVersionCountAsync(world, "manual_test_case", manualCase)).ShouldBe(2);
    }

    /// <summary>
    /// AC8/AC10: an unknown entity, an unknown version, another project's entity or version, another org's version and a
    /// version whose entity row is gone are all the same <c>404</c> — cross-project and cross-tenant addressing is
    /// indistinguishable from absence.
    /// </summary>
    [Fact]
    public async Task Restore_UnknownEntityOrVersion_404()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");

        // An id no route ever returned.
        var unknown = await SendAsync(world, world.Restore(world.Node(Guid.CreateVersion7())), ifMatch: "\"1\"");
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.ErrorCode.ShouldBe("not_found");

        var unknownVersion = await SendAsync(world, world.VersionRestore(Guid.CreateVersion7()), ifMatch: null);
        unknownVersion.Status.ShouldBe(HttpStatusCode.NotFound);
        unknownVersion.ErrorCode.ShouldBe("not_found");

        // A second project of the same org: its node and its version are absent from this project's routes (AC10).
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");
        var otherNodes = $"/v1/projects/{otherProject}/authoring/nodes";
        var foreign = await world.Client.SendAsync(
            HttpMethod.Post, otherNodes, world.As, new { kind = "module", title = "Billing" }, NewKey());
        foreign.Status.ShouldBe(HttpStatusCode.Created, foreign.ErrorMessage);
        var foreignNode = foreign.Id("node_id");
        (await world.Client.SendAsync(
            HttpMethod.Delete, $"{otherNodes}/{foreignNode:D}", world.As, idempotencyKey: NewKey(), ifMatch: "\"1\"")).Status
            .ShouldBe(HttpStatusCode.NoContent);
        var foreignVersion = await VersionIdAsync(world, foreignNode, versionNo: 1);

        var crossProject = await SendAsync(world, world.Restore(world.Node(foreignNode)), ifMatch: "\"2\"");
        crossProject.Status.ShouldBe(HttpStatusCode.NotFound);
        crossProject.ErrorCode.ShouldBe("not_found");

        var crossProjectVersion = await SendAsync(world, world.VersionRestore(foreignVersion), ifMatch: null);
        crossProjectVersion.Status.ShouldBe(HttpStatusCode.NotFound);
        crossProjectVersion.ErrorCode.ShouldBe("not_found");

        // Another org entirely: a different founder owns it, so its node and its version are invisible from here.
        var stranger = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, stranger, "other");
        var otherOrgProject = await CreateProjectAsync(world.Client, stranger, otherOrg, "elsewhere");
        var strangerAs = TenancyApiFactory.Resolved(user: stranger, orgHeader: otherOrg);
        var otherOrgNodes = $"/v1/projects/{otherOrgProject}/authoring/nodes";
        var elsewhere = await world.Client.SendAsync(
            HttpMethod.Post, otherOrgNodes, strangerAs, new { kind = "module", title = "Elsewhere" }, NewKey());
        elsewhere.Status.ShouldBe(HttpStatusCode.Created, elsewhere.ErrorMessage);
        var elsewhereNode = elsewhere.Id("node_id");
        (await world.Client.SendAsync(
            HttpMethod.Delete, $"{otherOrgNodes}/{elsewhereNode:D}", strangerAs, idempotencyKey: NewKey(), ifMatch: "\"1\"")).Status
            .ShouldBe(HttpStatusCode.NoContent);

        var crossOrgNode = await SendAsync(world, world.Restore(world.Node(elsewhereNode)), ifMatch: "\"2\"");
        crossOrgNode.Status.ShouldBe(HttpStatusCode.NotFound);
        crossOrgNode.ErrorCode.ShouldBe("not_found");

        var crossOrgVersion = await SendAsync(world, world.VersionRestore(await VersionIdAsync(world, elsewhereNode, versionNo: 1)), ifMatch: null);
        crossOrgVersion.Status.ShouldBe(HttpStatusCode.NotFound);
        crossOrgVersion.ErrorCode.ShouldBe("not_found");

        // A version whose entity row is gone: the app role only soft-deletes, so the row is removed by the owner erasure
        // path (AC6) and the restore of its version is the 404 of AC8, never a resurrection.
        var doomed = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Archived");
        (await DeleteAsync(world, world.Node(doomed), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        var doomedVersion = await VersionIdAsync(world, doomed, versionNo: 1);

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org, world.Project))
        {
            await owner.ExecuteAsync(
                "delete from public.feature_node where org_id = @org_id and id = @id",
                ("org_id", world.Org), ("id", doomed));
            await owner.CommitAsync();
        }

        var gone = await SendAsync(world, world.VersionRestore(doomedVersion), ifMatch: null);
        gone.Status.ShouldBe(HttpStatusCode.NotFound);
        gone.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// AC19 for the restore routes: <c>If-Match</c> is compared with the <b>locked</b> row's version before anything is
    /// written, so of two restores that present the same tag exactly one lands and the other is <c>412</c> with the
    /// current version in <c>details</c>; an absent tag is the same 422 the other writes answer.
    /// </summary>
    [Fact]
    public async Task Restore_StalePrecondition_412()
    {
        await using var world = await StartRestoreAsync();

        var node = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        (await DeleteAsync(world, world.Node(node), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        // The first restore consumes the tombstone's tag.
        var first = await SendAsync(world, world.Restore(world.Node(node)), ifMatch: "\"2\"");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Body.GetProperty("version").GetInt32().ShouldBe(3);

        // The same tag again — the exact request a client retries after reading nothing — is stale, not a second restore.
        var stale = await SendAsync(world, world.Restore(world.Node(node)), ifMatch: "\"2\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        Reasons(stale).ShouldContain("stale");
        Paths(stale).ShouldContain("/If-Match");
        Paths(stale).ShouldContain(path => path != null && path.EndsWith("version", StringComparison.Ordinal));

        // Nothing was written by the refusal: one restore row, and the row still sits at version 3.
        (await RestoreVersionCountAsync(world, "feature_node", node)).ShouldBe(3);
        (await EntityStateAsync(world, "feature_node", node)).ShouldBe("true|0|3");

        // A missing If-Match is the 422 of AC19, with the member the caller omitted.
        var missing = await SendAsync(world, world.Restore(world.Node(node)), ifMatch: null);
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.ErrorCode.ShouldBe("request_invalid");
        Reasons(missing).ShouldContain("required");
        Paths(missing).ShouldContain("/If-Match");

        // Two restores that start from the same tag: one appends, the other answers 412 — the comparison is under the
        // entity lock, so the loser cannot append a second time.
        var raced = await CreateNodeAsync(world, parent: null, kind: "module", title: "Refunds");
        (await DeleteAsync(world, world.Node(raced), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var both = await Task.WhenAll(
            SendAsync(world, world.Restore(world.Node(raced)), ifMatch: "\"2\""),
            SendAsync(world, world.Restore(world.Node(raced)), ifMatch: "\"2\""));

        both.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        both.Count(answer => answer.Status == HttpStatusCode.PreconditionFailed).ShouldBe(1);
        both.Single(answer => answer.Status == HttpStatusCode.OK).Body.GetProperty("version").GetInt32().ShouldBe(3);
        (await RestoreVersionCountAsync(world, "feature_node", raced)).ShouldBe(3, "exactly one of the two restores appended");
    }

    /// <summary>
    /// Hand-over (a) / OI-13-128: the restore takes the sibling group's advisory lock before it resolves <c>max + 1</c>.
    /// Holding that key in another session therefore parks the restore on it, and the writer's 5 s <c>lock_timeout</c>
    /// turns the wait into the retryable <c>503 unavailable</c> of §5 — never a 500 and never a silent append.
    /// </summary>
    [Fact]
    public async Task Restore_GroupLockHeld_Retryable503()
    {
        await using var world = await StartRestoreAsync();

        var node = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        (await DeleteAsync(world, world.Node(node), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        // The key a create, a move-into-group and the reorder of this root group take (AuthoringLockGuard.SiblingGroupKey).
        var groupKey = "authoring_group:" + world.Project.ToString("D") + ":root";

        await using var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org, world.Project);
        await holder.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended(@key, 0))", ("key", groupKey));

        var blocked = await SendAsync(world, world.Restore(world.Node(node)), ifMatch: "\"2\"");

        blocked.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, "a group lock held past lock_timeout is the retryable 503 of §5");
        blocked.ErrorCode.ShouldBe("unavailable");

        // The holder's transaction-scoped lock dies with its commit, so the retry the 503 promises is a plain success.
        await holder.CommitAsync();

        var retried = await SendAsync(world, world.Restore(world.Node(node)), ifMatch: "\"2\"");
        retried.Status.ShouldBe(HttpStatusCode.OK, retried.ErrorMessage);
        retried.Body.GetProperty("version").GetInt32().ShouldBe(3);
    }

    /// <summary>
    /// Hand-over (a) under real concurrency: a restore racing a create into the same sibling group must not answer a
    /// <c>500</c> — the retryable <c>503</c> is the only permitted refusal, a lost position is never a <c>409</c> or a
    /// <c>422</c>, and no two live rows of the group end up on one position.
    /// </summary>
    /// <remarks>
    /// This test races two real writers and pins the outcome; it does <b>not</b> prove by itself that the restore takes the
    /// group lock — two orderings it cannot force end in the same answers. That proof is
    /// <see cref="Restore_GroupLockHeld_Retryable503"/>, which holds the key in another session and sees the restore park
    /// on it until the writer's <c>lock_timeout</c>.
    /// </remarks>
    [Fact]
    public async Task Restore_RacingCreate_SameGroup_NoDuplicatePosition()
    {
        await using var world = await StartRestoreAsync();

        var first = await CreateNodeAsync(world, parent: null, kind: "module", title: "A");
        _ = await CreateNodeAsync(world, parent: null, kind: "module", title: "B");
        (await DeleteAsync(world, world.Node(first), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        // Both write the root group: the restore re-appends the tombstone and the create appends at the group's max + 1.
        var answers = await Task.WhenAll(
            SendAsync(world, world.Restore(world.Node(first)), ifMatch: "\"2\""),
            world.Client.SendAsync(HttpMethod.Post, world.Nodes, world.As, new { kind = "module", title = "C" }, NewKey()));

        answers.ShouldNotContain(
            answer => answer.Status == HttpStatusCode.InternalServerError,
            "the group lock serialises the two writers, so neither can surface a raw unique violation");
        answers.ShouldAllBe(
            answer => answer.Status == HttpStatusCode.OK
                || answer.Status == HttpStatusCode.Created
                || answer.Status == HttpStatusCode.ServiceUnavailable,
            "a lost race is the retryable 503, never a 409 or a 422");

        // The AC2 invariant holds either way: one live row per position inside the group (null parent = the roots).
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"""
             select count(*)::integer
               from (select position from public.feature_node
                      where org_id = '{world.Org}' and project_id = '{world.Project}' and deleted_at is null
                      group by parent_id, position having count(*) > 1) duplicate
             """)).ShouldBe(0, "a restore never duplicates a live sibling position");
    }

    /// <summary>
    /// AC18 on the restore routes: exactly one audit row per effective mutation, none on an idempotent replay and none on
    /// a refusal. AC17's permission gate is the same as every other write route — a viewer holding only
    /// <c>authoring:read</c> is refused <c>403</c>.
    /// </summary>
    [Fact]
    public async Task Restore_AuditOncePerSuccess_NoneOnReplayOrRefusal()
    {
        await using var world = await StartRestoreAsync();

        var node = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        (await DeleteAsync(world, world.Node(node), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var key = NewKey();
        var restored = await world.Client.SendAsync(
            HttpMethod.Post, world.Restore(world.Node(node)), world.As, idempotencyKey: key, ifMatch: "\"2\"");
        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        (await AuditCountAsync(world, "authoring.node.restore")).ShouldBe(1);

        // The same key replays the stored answer instead of appending a second version or a second audit row.
        var replay = await world.Client.SendAsync(
            HttpMethod.Post, world.Restore(world.Node(node)), world.As, idempotencyKey: key, ifMatch: "\"2\"");
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetProperty("version").GetInt32().ShouldBe(3);
        (await RestoreVersionCountAsync(world, "feature_node", node)).ShouldBe(3);
        (await AuditCountAsync(world, "authoring.node.restore")).ShouldBe(1);

        // A refusal writes no audit row either: the entity is live now, so the restore is the 409 of AC8.
        var refused = await SendAsync(world, world.Restore(world.Node(node)), ifMatch: "\"3\"");
        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("invalid_transition");
        (await AuditCountAsync(world, "authoring.node.restore")).ShouldBe(1);

        // AC17: a member with the viewer role holds authoring:read only, so the write is refused before any state is read.
        var viewer = await AddRestoreMemberAsync(world, "viewer");
        var asViewer = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        var forbidden = await world.Client.SendAsync(
            HttpMethod.Post, world.Restore(world.Node(node)), asViewer, idempotencyKey: NewKey(), ifMatch: "\"3\"");
        forbidden.Status.ShouldBe(HttpStatusCode.Forbidden);
        forbidden.ErrorCode.ShouldBe("forbidden");
        (await AuditCountAsync(world, "authoring.node.restore")).ShouldBe(1);
    }

    /// <summary>
    /// AC8's parent rule on route 24: re-applying a version whose parent is still soft-deleted is the same
    /// <c>422 authoring_parent_invalid</c> / <c>details[].reason = parent_deleted</c> the entity routes answer — the named
    /// version supplies the parent the guard reads — and nothing is written (review F6).
    /// </summary>
    [Fact]
    public async Task Restore_Version_ChildUnderDeletedParent_422()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");

        // A node under a deleted node: the child is tombstoned first, then the now-empty parent, so both versions the
        // route can name describe a row whose parent is a tombstone.
        var category = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Cards");
        var child = await CreateNodeAsync(world, parent: category, kind: "sub_category", title: "Refunds");
        var childVersion = await VersionIdAsync(world, child, versionNo: 1);
        (await DeleteAsync(world, world.Node(child), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(category), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var orphanNode = await SendAsync(world, world.VersionRestore(childVersion), ifMatch: null);
        orphanNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        orphanNode.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(orphanNode).ShouldContain("parent_deleted");

        // Nothing written: the child keeps its tombstone at the version its delete left and its two-row history.
        (await EntityStateAsync(world, "feature_node", child)).ShouldBe("false|0|2");
        (await RestoreVersionCountAsync(world, "feature_node", child)).ShouldBe(2);

        // The same guard one rank down: an item re-applied by route 24 under a deleted node. Items hang under a
        // category (13.1.1 AC1), so the deleted node is one.
        var branch = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Chargebacks");
        var item = await CreateItemAsync(world, branch, "Refund is visible");
        var itemVersion = await VersionIdAsync(world, item, versionNo: 1);
        (await DeleteAsync(world, world.Item(item), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(branch), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var orphanItem = await SendAsync(world, world.VersionRestore(itemVersion), ifMatch: null);
        orphanItem.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        orphanItem.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(orphanItem).ShouldContain("parent_deleted");
        (await RestoreVersionCountAsync(world, "checklist_item", item)).ShouldBe(2);
    }

    /// <summary>
    /// AC19 before AC8's parent rule (review F6): a restore that presents a tag the row no longer has is <c>412</c> even
    /// when its parent is still soft-deleted — the comparison runs under the entity lock, ahead of the parent guard — on
    /// all three entity routes, and the same request with the tombstone's tag is then the <c>422 parent_deleted</c>.
    /// </summary>
    [Fact]
    public async Task Restore_StaleTag_BeatsParentDeleted()
    {
        await using var world = await StartRestoreAsync();

        // Route 6: a node whose parent is deleted, presented with the create's tag instead of the tombstone's.
        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");
        var category = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Cards");
        var child = await CreateNodeAsync(world, parent: category, kind: "sub_category", title: "Refunds");
        (await DeleteAsync(world, world.Node(child), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(category), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var staleNode = await SendAsync(world, world.Restore(world.Node(child)), ifMatch: "\"1\"");
        staleNode.Status.ShouldBe(HttpStatusCode.PreconditionFailed, "the stale tag answers before the parent rule is consulted");
        staleNode.ErrorCode.ShouldBe("precondition_failed");
        Reasons(staleNode).ShouldContain("stale");
        (await RestoreVersionCountAsync(world, "feature_node", child)).ShouldBe(2);

        var orphanNode = await SendAsync(world, world.Restore(world.Node(child)), ifMatch: "\"2\"");
        orphanNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        orphanNode.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(orphanNode).ShouldContain("parent_deleted");

        // Route 11: the same pair one rank down. Items hang under a category (13.1.1 AC1).
        var branch = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Chargebacks");
        var item = await CreateItemAsync(world, branch, "Refund is visible");
        (await DeleteAsync(world, world.Item(item), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(branch), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var staleItem = await SendAsync(world, world.Restore(world.Item(item)), ifMatch: "\"1\"");
        staleItem.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        staleItem.ErrorCode.ShouldBe("precondition_failed");
        (await RestoreVersionCountAsync(world, "checklist_item", item)).ShouldBe(2);

        var orphanItem = await SendAsync(world, world.Restore(world.Item(item)), ifMatch: "\"2\"");
        orphanItem.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(orphanItem).ShouldContain("parent_deleted");

        // Route 18: and once more for a case under a deleted item.
        var holder = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Refunds");
        var liveItem = await CreateItemAsync(world, holder, "Chargeback is filed");
        var manualCase = await CreateCaseAsync(world, liveItem, "File the chargeback");
        (await DeleteAsync(world, world.Case(manualCase), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Item(liveItem), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);

        var staleCase = await SendAsync(world, world.Restore(world.Case(manualCase)), ifMatch: "\"1\"");
        staleCase.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        staleCase.ErrorCode.ShouldBe("precondition_failed");
        (await RestoreVersionCountAsync(world, "manual_test_case", manualCase)).ShouldBe(2);

        var orphanCase = await SendAsync(world, world.Restore(world.Case(manualCase)), ifMatch: "\"2\"");
        orphanCase.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(orphanCase).ShouldContain("parent_deleted");
    }

    /// <summary>
    /// AC19/AC8 on the two child routes, and route 24 on a live entity (review F6): a live item or case restored with a
    /// stale tag is <c>412</c>, with its current tag it is the <c>409 invalid_transition</c> of "not soft-deleted", and
    /// route 24 — which carries no tag and names the state instead — re-appends a live entity as a fresh version rather
    /// than refusing it. Every refusal writes nothing.
    /// </summary>
    [Fact]
    public async Task Restore_ItemAndCase_StaleAndLive()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");
        var category = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Cards");

        // A live item at version 2: the create is version 1, the edit the tag the caller can be stale against.
        var item = await CreateItemAsync(world, category, "Pay online");
        var edited = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As, new { title = "Pay online (card)" }, NewKey(), "\"1\"");
        edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);

        var staleItem = await SendAsync(world, world.Restore(world.Item(item)), ifMatch: "\"1\"");
        staleItem.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        staleItem.ErrorCode.ShouldBe("precondition_failed");
        Reasons(staleItem).ShouldContain("stale");

        var liveItem = await SendAsync(world, world.Restore(world.Item(item)), ifMatch: "\"2\"");
        liveItem.Status.ShouldBe(HttpStatusCode.Conflict, "a live entity is the 409 of AC8, not a silent second restore");
        liveItem.ErrorCode.ShouldBe("invalid_transition");
        (await RestoreVersionCountAsync(world, "checklist_item", item)).ShouldBe(2, "neither refusal appended a version");

        // Route 24 takes no tag: naming a version of a live entity re-applies it as one more version.
        var reapplied = await SendAsync(world, world.VersionRestore(await VersionIdAsync(world, item, versionNo: 1)), ifMatch: null);
        reapplied.Status.ShouldBe(HttpStatusCode.OK, reapplied.ErrorMessage);
        reapplied.Body.GetProperty("version").GetInt32().ShouldBe(3);
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select title from public.checklist_item where id = '{item:D}'"))
            .ShouldBe("Pay online", "the named version's snapshot is what the route re-applies");

        // A live case answers the same pair on route 18.
        var caseItem = await CreateItemAsync(world, category, "Card is charged");
        var manualCase = await CreateCaseAsync(world, caseItem, "Charge the card");
        var caseEdited = await world.Client.SendAsync(
            HttpMethod.Patch, world.Case(manualCase), world.As, new { title = "Charge the card once" }, NewKey(), "\"1\"");
        caseEdited.Status.ShouldBe(HttpStatusCode.OK, caseEdited.ErrorMessage);

        var staleCase = await SendAsync(world, world.Restore(world.Case(manualCase)), ifMatch: "\"1\"");
        staleCase.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        staleCase.ErrorCode.ShouldBe("precondition_failed");

        var liveCase = await SendAsync(world, world.Restore(world.Case(manualCase)), ifMatch: "\"2\"");
        liveCase.Status.ShouldBe(HttpStatusCode.Conflict);
        liveCase.ErrorCode.ShouldBe("invalid_transition");
        (await RestoreVersionCountAsync(world, "manual_test_case", manualCase)).ShouldBe(2);
    }

    /// <summary>
    /// AC2's cap on the restore path (review F3): a restore re-appends its row as a live member, so it grows its target
    /// group exactly like a create or a move-into-group does. A restore into a group of 999 live siblings lands — that is
    /// the 1 000 the reorder accepts — and the next one, routes 6 and 24 alike, answers <c>422 request_invalid</c> with
    /// <c>details[].path = /parent_id</c> and <c>details[].reason = children_limit_exceeded</c> and writes nothing: the
    /// tombstone, its position and its history stay as the delete left them.
    /// </summary>
    [Fact]
    public async Task Restore_NodeGroupFull_422()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");

        // Both tombstones are made before the group fills: once it is at the cap a create into it is refused too.
        var first = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");
        var second = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Refunds");
        (await DeleteAsync(world, world.Node(first), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(world, world.Node(second), version: 1)).Status.ShouldBe(HttpStatusCode.NoContent);
        var secondVersion = await VersionIdAsync(world, second, versionNo: 1);

        // 999 live siblings in one statement — the group a reorder can still take (ReorderNodesCommandValidator.
        // MaxOrderedIds), seeded the way the create-side cap test seeds it.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
             insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
             select cast(md5('s80-restore-cap-' || g) as uuid),
                    '{world.Org:D}',
                    '{world.Project:D}',
                    '{module:D}',
                    'feature',
                    'Seeded ' || g,
                    g - 1
               from generate_series(1, 999) as g
             """);

        // The restore of `first` re-appends it: its old position 0 is taken, so it lands at the group's max + 1.
        var filled = await SendAsync(world, world.Restore(world.Node(first)), ifMatch: "\"2\"");
        filled.Status.ShouldBe(HttpStatusCode.OK, filled.ErrorMessage);
        filled.Body.GetProperty("version").GetInt32().ShouldBe(3);
        (await EntityStateAsync(world, "feature_node", first)).ShouldBe("true|999|3", "the old position was taken, so max + 1");
        (await LiveChildrenAsync(world, module)).ShouldBe(1000);

        // The group is at the cap now: the second restore is one live member too many.
        var refused = await SendAsync(world, world.Restore(world.Node(second)), ifMatch: "\"2\"");
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Paths(refused).ShouldContain("/parent_id");
        Reasons(refused).ShouldContain("children_limit_exceeded");

        // Route 24 re-applies the same snapshot and meets the same cap.
        var refusedVersion = await SendAsync(world, world.VersionRestore(secondVersion), ifMatch: null);
        refusedVersion.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refusedVersion.ErrorCode.ShouldBe("request_invalid");
        Paths(refusedVersion).ShouldContain("/parent_id");
        Reasons(refusedVersion).ShouldContain("children_limit_exceeded");

        // Nothing written by either refusal: the tombstone keeps its position and its delete version, its history stays
        // two rows long, and the group is still the 1 000 live members the restore of `first` produced.
        (await EntityStateAsync(world, "feature_node", second)).ShouldBe("false|1|2");
        (await RestoreVersionCountAsync(world, "feature_node", second)).ShouldBe(2);
        (await LiveChildrenAsync(world, module)).ShouldBe(1000);
    }

    /// <summary>
    /// AC2's cap does not refuse a restore that adds no member (review F3, the gap the delta named): route 24 re-applies
    /// a version of a LIVE node inside the group it already sits in, which is at the 1 000 cap. The write grows nothing,
    /// so it lands as a new version rather than <c>children_limit_exceeded</c>.
    /// </summary>
    [Fact]
    public async Task Restore_Version_LiveNodeInOwnFullGroup_NotRefused()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");
        var versionOne = await VersionIdAsync(world, feature, versionNo: 1);

        await SeedSiblingsAsync(world, module, count: 999, firstPosition: 1, tag: "own-group");
        (await LiveChildrenAsync(world, module)).ShouldBe(1000);

        var answer = await SendAsync(world, world.VersionRestore(versionOne), ifMatch: null);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await RestoreVersionCountAsync(world, "feature_node", feature)).ShouldBe(2);
        (await LiveChildrenAsync(world, module)).ShouldBe(1000);
    }

    /// <summary>
    /// AC2's cap on route 24's move-in branch (review F3, the gap the delta named): the version's snapshot names group A,
    /// the live row now sits in group B, and A is at 1 000 live members, so re-applying the snapshot would bring a 1 001st
    /// into A. It answers <c>422 request_invalid</c> / <c>/parent_id</c> / <c>children_limit_exceeded</c> and writes
    /// nothing: the row stays in B at its own version and its history keeps its one row.
    /// </summary>
    [Fact]
    public async Task Restore_Version_MoveIntoFullGroup_422()
    {
        await using var world = await StartRestoreAsync();

        var groupA = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var groupB = await CreateNodeAsync(world, parent: null, kind: "module", title: "Billing");
        var feature = await CreateNodeAsync(world, parent: groupA, kind: "feature", title: "Payment");
        var versionOne = await VersionIdAsync(world, feature, versionNo: 1);

        // The row moves to B behind the history's back (an owner-role UPDATE, as the delete tests do for a gone row), so
        // the snapshot of version 1 (parent A) and the stored row (parent B) differ; A then fills to the cap.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.feature_node set parent_id = '{groupB:D}', position = 0 where id = '{feature:D}'");
        await SeedSiblingsAsync(world, groupA, count: 1000, firstPosition: 0, tag: "move-in");
        (await LiveChildrenAsync(world, groupA)).ShouldBe(1000);

        var refused = await SendAsync(world, world.VersionRestore(versionOne), ifMatch: null);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        Paths(refused).ShouldContain("/parent_id");
        Reasons(refused).ShouldContain("children_limit_exceeded");

        (await RestoreVersionCountAsync(world, "feature_node", feature)).ShouldBe(1);
        (await LiveChildrenAsync(world, groupA)).ShouldBe(1000);
        (await LiveChildrenAsync(world, groupB)).ShouldBe(1);
    }

    private Task SeedSiblingsAsync(RestoreWorld world, Guid parent, int count, int firstPosition, string tag) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"""
             insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
             select cast(md5('s80-{tag}-' || g) as uuid),
                    '{world.Org:D}',
                    '{world.Project:D}',
                    '{parent:D}',
                    'feature',
                    'Seeded ' || g,
                    g - 1 + {firstPosition}
               from generate_series(1, {count}) as g
             """);

    /// <summary>
    /// AC8 against a retired parent (review F8): route 24 re-applies a version of a live case whose checklist item is
    /// <c>retired</c>, so the case parent guard answers <c>409 invalid_transition</c> — "restore the item first" — and
    /// nothing is written: the case stays live at its own version and its history keeps its one row.
    /// </summary>
    [Fact]
    public async Task Restore_Version_RetiredItemParent_409()
    {
        await using var world = await StartRestoreAsync();

        var module = await CreateNodeAsync(world, parent: null, kind: "module", title: "Checkout");
        var feature = await CreateNodeAsync(world, parent: module, kind: "feature", title: "Payment");
        var category = await CreateNodeAsync(world, parent: feature, kind: "category", title: "Cards");
        var item = await CreateItemAsync(world, category, "Pay by card");
        var manualCase = await CreateCaseAsync(world, item, "Card is charged");
        var caseVersion = await VersionIdAsync(world, manualCase, versionNo: 1);

        // §6/AC20: active -> retired, with the reason the transition requires.
        var retired = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As, new { state = "retired", reason = "obsolete" }, NewKey(), "\"1\"");
        retired.Status.ShouldBe(HttpStatusCode.OK, retired.ErrorMessage);

        var refused = await SendAsync(world, world.VersionRestore(caseVersion), ifMatch: null);
        refused.Status.ShouldBe(HttpStatusCode.Conflict, "a retired item takes no new content, a re-applied version included");
        refused.ErrorCode.ShouldBe("invalid_transition");

        // The wording proves it is the case parent guard that answered (13.1.1 §6: a retired item takes no new content),
        // not the 409 of an entity that is simply not soft-deleted.
        refused.ErrorMessage.ShouldNotBeNull().ShouldContain("a retired checklist item takes no new content");

        // Nothing written: the case is still live at version 1 with its single create row, and the item is still retired.
        (await EntityStateAsync(world, "manual_test_case", manualCase)).ShouldBe("true|1");
        (await RestoreVersionCountAsync(world, "manual_test_case", manualCase)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select state from public.checklist_item where id = '{item:D}'"))
            .ShouldBe("retired");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The 00 §9.2 <c>details[].reason</c> values of an error envelope.</summary>
    private static IReadOnlyList<string?> Reasons(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.TryGetProperty("reason", out var reason) ? reason.GetString() : null)];

    /// <summary>The 00 §9.2 <c>details[].path</c> values of an error envelope: the member the answer blames.</summary>
    private static IReadOnlyList<string?> Paths(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.TryGetProperty("path", out var path) ? path.GetString() : null)];

    private async Task<RestoreWorld> StartRestoreAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new RestoreWorld(api, client, database, user, org, project);
    }

    /// <summary>One restore request: <c>Idempotency-Key</c> always, <c>If-Match</c> when the route declares one.</summary>
    private static Task<ApiAnswer> SendAsync(RestoreWorld world, string url, string? ifMatch) =>
        world.Client.SendAsync(HttpMethod.Post, url, world.As, idempotencyKey: NewKey(), ifMatch: ifMatch);

    /// <summary>Routes 2's create, answered with the new node id.</summary>
    private static async Task<Guid> CreateNodeAsync(RestoreWorld world, Guid? parent, string kind, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { parent_id = parent, kind, title }, NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("node_id");
    }

    /// <summary>Route 8's create, answered with the new item id.</summary>
    private static async Task<Guid> CreateItemAsync(RestoreWorld world, Guid node, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = node, title, priority = "high" }, NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("item_id");
    }

    /// <summary>Route 13's create, answered with the new case id.</summary>
    private static async Task<Guid> CreateCaseAsync(RestoreWorld world, Guid item, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Cases,
            world.As,
            new
            {
                checklist_item_id = item,
                title,
                steps = new[] { new { no = 1, action = "Open the page", expected = "The page is shown" } },
                priority = "high",
            },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("case_id");
    }

    /// <summary>Routes 5, 10 and 16: the soft delete that makes the restore meaningful.</summary>
    private static Task<ApiAnswer> DeleteAsync(RestoreWorld world, string url, int version) =>
        world.Client.SendAsync(HttpMethod.Delete, url, world.As, idempotencyKey: NewKey(), ifMatch: $"\"{version}\"");

    /// <summary>The id of one version row of an entity, read where the timeline stores it (no route is needed for this).</summary>
    private async Task<Guid> VersionIdAsync(RestoreWorld world, Guid entityId, int versionNo) =>
        await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.authoring_version where entity_id = '{entityId:D}' and version_no = {versionNo}");

    /// <summary>The number of <c>authoring_version</c> rows of one entity (AC5: one row per change, none rewritten).</summary>
    private async Task<int> RestoreVersionCountAsync(RestoreWorld world, string kind, Guid entityId) =>
        await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::integer from public.authoring_version where entity_kind = '{kind}' and entity_id = '{entityId:D}'");

    /// <summary>The number of live children of one node: AC2's sibling group, read where the tree stores it.</summary>
    private async Task<int> LiveChildrenAsync(RestoreWorld world, Guid parent) =>
        await _fixture.ScalarAsync<int>(
            world.Database,
            $"""
             select count(*)::integer
               from public.feature_node
              where org_id = '{world.Org:D}' and project_id = '{world.Project:D}'
                and parent_id = '{parent:D}' and deleted_at is null
             """);

    /// <summary>The newest version row of an entity as <c>change_kind|version_no</c>.</summary>
    private async Task<string> LastVersionAsync(RestoreWorld world, Guid entityId) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select change_kind || '|' || version_no::text from public.authoring_version where entity_id = '{entityId:D}' order by version_no desc limit 1");

    /// <summary>One version's stored snapshot, byte for byte.</summary>
    private async Task<string> VersionContentAsync(RestoreWorld world, Guid entityId, int versionNo) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select content::text from public.authoring_version where entity_id = '{entityId:D}' and version_no = {versionNo}");

    /// <summary>
    /// A row as <c>(deleted_at is null)|position|version</c>, or <c>(deleted_at is null)|version</c> for a kind without a
    /// position: <c>manual_test_case</c> carries none (0039_authoring_tree.sql:287-303; only the tree and the checklist
    /// item are ordered by a position).
    /// </summary>
    private async Task<string> EntityStateAsync(RestoreWorld world, string kind, Guid entityId) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            kind == "manual_test_case"
                ? $"select (deleted_at is null)::text || '|' || version::text from public.{kind} where id = '{entityId:D}'"
                : $"select (deleted_at is null)::text || '|' || position::text || '|' || version::text from public.{kind} where id = '{entityId:D}'");

    /// <summary>The number of audit rows of one action in this org (AC18).</summary>
    private async Task<int> AuditCountAsync(RestoreWorld world, string action) =>
        await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::integer from public.audit_log where org_id = '{world.Org}' and action = '{action}'");

    /// <summary>An active member of the org holding one built-in role, granted by the founder through the API.</summary>
    private async Task<Guid> AddRestoreMemberAsync(RestoreWorld world, string roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
             insert into public.membership (id, org_id, user_id, invited_email, status)
             values ('{membership}', '{world.Org}', '{user}', 'viewer-{user:N}@example.test', 'active')
             """);

        var role = await _fixture.ScalarAsync<Guid>(
            world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{world.Org}/memberships/{membership}/roles", world.As, new { role_id = role }, NewKey());

        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return user;
    }
}

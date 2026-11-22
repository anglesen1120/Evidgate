using System.Diagnostics;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Import;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringImporterTests
{
    /// <summary>AC15: an unchanged re-import keeps the raw <c>content_hash</c> — one version row, the same digest.</summary>
    [Fact]
    public async Task Reimport_SameContentHashes()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var code = await CaseCodeAsync(world);

        var before = await HashesAsync(world, "manual_test_case");
        before.Count.ShouldBe(1);

        (await ImportAsync(world, world.Project, Single(code))).IsSuccess.ShouldBeTrue();

        (await HashesAsync(world, "manual_test_case")).ShouldBe(before);

        // The same holds for the three nodes and the item: nothing was appended for any of them.
        (await HashesAsync(world, "feature_node")).Count.ShouldBe(3);
        (await HashesAsync(world, "checklist_item")).Count.ShouldBe(1);
    }

    /// <summary>Plan P2: the number of statements one import sends does not depend on how many rows it carries.</summary>
    [Fact]
    public async Task Import_StatementCount()
    {
        await using var world = await StartAsync();
        var small = await CreateProjectAsync(world.Client, world.User, world.Org, "small");
        var large = await CreateProjectAsync(world.Client, world.User, world.Org, "large");

        var smallCount = await StatementsAsync(world, small, File(10));
        var largeCount = await StatementsAsync(world, large, File(200));

        smallCount.ShouldBe(largeCount);
        // A small constant: the writer's own bound is a handful of statements per change, so a per-row path would be
        // hundreds here. The ceiling guards the shape of the path, not a byte count.
        largeCount.ShouldBeLessThan(40);
    }

    /// <summary>The 5 000-row cap: a full-size file applies inside a generous wall time, through one allocation block.</summary>
    [Fact]
    public async Task Import_5000Rows_Completes()
    {
        // A wall-clock bound that a busy shared host would move: the test owns the database it times on.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated.Name);
        var rows = File(1_000);

        rows.Count.ShouldBe(ImportLimits.MaxDataRows);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await ImportAsync(world, world.Project, rows);
        stopwatch.Stop();

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Counts.Created.ShouldBe(5_000);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(120), "a 5 000-row file applies well inside two minutes");

        (await CountAsync(world, "feature_node")).ShouldBe(3_000);
        (await CountAsync(world, "checklist_item")).ShouldBe(1_000);
        (await CountAsync(world, "manual_test_case")).ShouldBe(1_000);

        // One block for the 1 000 code-less cases: the counter stands just past it and no code was handed out twice.
        (await CounterAsync(world)).ShouldBe(1_001L);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(distinct code)::int from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).ShouldBe(1_000);
    }

    /// <summary>
    /// A sibling group another writer holds is the retryable <c>503</c>, never a hang and never a write: the apply takes
    /// the same advisory key the create, move and reorder paths take, under the writer-wide <c>lock_timeout</c> (13.1.1
    /// §5, findings F/I).
    /// </summary>
    [Fact]
    public async Task SiblingGroupLockedElsewhere_ImportAnswers503()
    {
        await using var world = await StartAsync();

        // The root node group: the file's module is appended there, so the apply must take this key before it writes.
        var key = AuthoringLockGuard.SiblingGroupKey(
            world.Project,
            null,
            AuthoringLockGuard.SiblingGroup.FeatureNode);

        await using var holder = await TenantScope.BeginAsync(
            _fixture.OwnerConnectionString(world.Database),
            world.Org);
        await holder.ExecuteAsync(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0))",
            ("key", key));

        var result = await ImportAsync(world, world.Project, Single());

        result.IsSuccess.ShouldBeFalse("a held sibling group is not a reason to wait for ever");
        result.Error!.Status.ShouldBe(503);
        (await CountAsync(world, "feature_node")).ShouldBe(0);
        (await CountAsync(world, "authoring_version")).ShouldBe(0);
    }

    /// <summary>
    /// Delta R1/R2: the parent item of a case row is locked <c>for share</c> — the file does not write it — so a writer that
    /// holds that row <c>for update</c> still serialises the import: the wait meets the writer's 5 s <c>lock_timeout</c> and
    /// the answer is the retryable 503, with nothing written.
    /// </summary>
    [Fact]
    public async Task ParentItemHeldForUpdate_ImportAnswers503()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var (_, itemId, code) = await CaseIdentityAsync(world);
        var versions = await VersionCountAsync(world);

        await using var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);
        await holder.ExecuteAsync(
            "select 1 from public.checklist_item where id = @id for update",
            ("id", itemId));

        var clock = Stopwatch.StartNew();
        var refused = await ImportAsync(world, world.Project, Single(code: code, title: "Case 1.1 edited"));
        clock.Stop();

        refused.IsSuccess.ShouldBeFalse("a held parent item is not a reason to wait for ever");
        refused.Error!.Status.ShouldBe(503);
        clock.Elapsed.ShouldBeGreaterThan(
            TimeSpan.FromSeconds(4),
            "the refusal is the writer's 5 s lock_timeout on the parent item, not an earlier contention");
        (await VersionCountAsync(world)).ShouldBe(versions, "the refused import wrote nothing");
        (await CaseTitlesAsync(world)).ShouldBe(["Case 1.1"]);
    }

    /// <summary>
    /// Delta R1, the mixed case the two item passes exist for: one item row this file <em>writes</em> and one parent item it
    /// only <em>reads</em> (an updated case hangs under the second). Both passes must run, and the share pass must not read a
    /// written item's absence from its own result as a stale plan.
    /// </summary>
    [Fact]
    public async Task WrittenItemAndReadParentItem_AreLockedInTheirOwnPasses()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world, world.Project);

        // An item is identified by its node and title, so an item *update* row is a content change under one title — a
        // priority here — never a rename (a rename is a create, AC15).
        IReadOnlyList<ImportRow> TwoLadders(string firstItemPriority, string secondCase) =>
        [
            ImportSample.Node(1, "m1", null, "module", "Module 1"),
            ImportSample.Node(2, "f1", "m1", "feature", "Feature 1"),
            ImportSample.Node(3, "n1", "f1", "category", "Category 1"),
            ImportSample.Item(4, "i1", "n1", "Item 1", priority: firstItemPriority),
            ImportSample.Case(5, "c1", "i1", "Case 1", ImportSample.Steps, code: prefix + "-001"),
            ImportSample.Node(6, "m2", null, "module", "Module 2"),
            ImportSample.Node(7, "f2", "m2", "feature", "Feature 2"),
            ImportSample.Node(8, "n2", "f2", "category", "Category 2"),
            ImportSample.Item(9, "i2", "n2", "Item 2"),
            ImportSample.Case(10, "c2", "i2", secondCase, ImportSample.Steps, code: prefix + "-002"),
        ];

        var created = await ImportAsync(world, world.Project, TwoLadders("high", "Case 2"));
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        // `Item 1` is written (an item update row); `Item 2` is only the parent of the updated case (a share-pass parent).
        var updated = await ImportAsync(world, world.Project, TwoLadders("low", "Case 2 edited"));

        updated.IsSuccess.ShouldBeTrue(updated.Error?.Message);
        updated.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 2, Restored: 0, Unchanged: 8));
    }

    /// <summary>
    /// Delta R1: a concurrent <c>for share</c> holder of the parent item does not block an import that only reads it —
    /// share and share never conflict, which is exactly why the read-only parent is locked that way instead of
    /// <c>for update</c>. This is the test that fails if the item rank goes back to one <c>for update</c> pass.
    /// </summary>
    [Fact]
    public async Task ParentItemHeldForShare_ImportProceeds()
    {
        // A wall-clock bound that a busy shared host would move: the test owns the database it times on.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated.Name);

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var (_, itemId, code) = await CaseIdentityAsync(world);

        await using var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);
        await holder.ExecuteAsync(
            "select 1 from public.checklist_item where id = @id for share",
            ("id", itemId));

        var clock = Stopwatch.StartNew();
        var updated = await ImportAsync(world, world.Project, Single(code: code, title: "Case 1.1 edited"));
        clock.Stop();

        updated.IsSuccess.ShouldBeTrue(updated.Error?.Message);
        clock.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(4),
            "share and share never conflict, so the import must not wait on the lock bound at all");
        (await CaseTitlesAsync(world)).ShouldBe(["Case 1.1 edited"]);
    }

    /// <summary>
    /// AC10: a file that carries another project's case codes can neither update nor collide with that project's rows — a
    /// code is unique per project, and the prefix rule refuses one that is not this project's.
    /// </summary>
    [Fact]
    public async Task CrossTenant_KeysCannotReachOtherProject()
    {
        await using var world = await StartAsync();
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();

        var code = await CaseCodeAsync(world);
        var versionsInProject = await CountAsync(world, "authoring_version");

        // 1. The same file with the first project's code, imported into the second project: refused by the prefix rule.
        var refused = await ImportAsync(world, other, Single(code));

        refused.IsSuccess.ShouldBeFalse("a code of another project cannot be imported");
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Details.ShouldNotBeNull();
        refused.Error.Details.ShouldContain(detail => detail.Path == "/code" && detail.Reason == "code_conflict" && detail.Index == 5);
        (await CountAsync(world, "manual_test_case", other)).ShouldBe(0);
        (await CountAsync(world, "authoring_version", other)).ShouldBe(0);

        // 2. The second project's own file leaves the first project exactly as it was.
        (await ImportAsync(world, other, Single())).IsSuccess.ShouldBeTrue();

        (await CountAsync(world, "manual_test_case", other)).ShouldBe(1);
        (await CountAsync(world, "manual_test_case")).ShouldBe(1);
        (await CountAsync(world, "authoring_version")).ShouldBe(versionsInProject);
        (await CaseCodeAsync(world)).ShouldBe(code);
    }

    /// <summary>
    /// AC4: a code outside the project's prefix is <c>code_conflict</c>; an unknown explicit code of the project is created
    /// and raises the counter, so the next allocation cannot collide with it.
    /// </summary>
    [Fact]
    public async Task CodeRules()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world, world.Project);

        // 1. A code of another prefix is refused before anything is written.
        var refused = await ImportAsync(world, world.Project, Single("ZZZZ-001"));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldNotBeNull();
        refused.Error.Details.ShouldContain(detail => detail.Path == "/code" && detail.Reason == "code_conflict" && detail.Index == 5);
        (await CountAsync(world, "feature_node")).ShouldBe(0);

        // 2. An explicit, unknown code of this project is created and the counter is raised past it.
        var created = await ImportAsync(world, world.Project, Single(prefix + "-042"));

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        created.Value!.Rows.Single(row => row.Kind == ImportRowKind.Case).Code.ShouldBe(prefix + "-042");
        (await CounterAsync(world)).ShouldBe(43L, "the explicit code raises the counter to max + 1");

        // 3. The case of a following file comes from the block that starts at the raised counter: 043, and never 042.
        var next = File(1);
        (await ImportAsync(world, world.Project, next)).IsSuccess.ShouldBeTrue();

        var codes = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select code from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' order by code");

        codes.ShouldBe([prefix + "-042", prefix + "-043"]);
    }
}

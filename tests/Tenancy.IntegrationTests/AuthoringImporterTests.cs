using System.Diagnostics;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC13/AC15 at the service level (13.1.1-T5): <see cref="AuthoringImporter"/> driven in a real tenant scope, so
/// atomicity, the one-batch-id rule, the dry run, the re-import identities and the statement budget are proven against
/// PostgreSQL rather than against a double.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the importer is constructed here instead of resolved.</b> Its route is the ENDPOINTS slice's, and this suite must
/// not depend on a registration that lands with it: the three dependencies it takes (the scoped
/// <see cref="ITenantDb"/>, the host's allocator and the steps validator) are resolved from the same request scope the
/// route would use, so the object under test is exactly the production graph.
/// </para>
/// <para>
/// <b>The statement budget reads the host's own log</b> (<c>TenancyApiFactory.Statements</c>), which captures every Npgsql
/// command of the run — the importer's, the planner's and the allocator's second connection alike. 50 rows and 1 000 rows
/// of the same shape must cost the <em>same</em> number of statements: that is plan P2's "one batched insert per entity
/// kind, never a per-row loop", and a per-row path would multiply the count by the row count.
/// </para>
/// </remarks>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringImporterTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringImporterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Dedicated.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        // Private database: the import timings are wall-clock thresholds that a busy shared host would trip.
        var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var database = dedicated.Name;
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project, dedicated);
    }

    /// <summary>Runs one import on the host's own object graph, inside one writable tenant scope of the world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="projectId">The project to import into.</param>
    /// <param name="rows">The rows, in file order.</param>
    /// <param name="dryRun">True for a dry run.</param>
    private static async Task<Result<ImportReport>> ImportAsync(
        World world,
        Guid projectId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun = false)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, projectId, false, cancellationToken);

        var importer = new AuthoringImporter(
            services.ServiceProvider.GetRequiredService<ITenantDb>(),
            services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>(),
            services.ServiceProvider.GetRequiredService<ManualCaseStepsValidator>());

        var result = await importer
            .ImportAsync(world.Org, projectId, null, rows, dryRun, cancellationToken)
;

        // A refusal wrote nothing, so committing it is the same as rolling it back — and committing keeps the "nothing
        // changed" assertions honest for a handler that commits whatever the answer was.
        await scope.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>Soft-deletes one entity through the writer, the one path AC7 allows.</summary>
    /// <param name="world">The world.</param>
    /// <param name="kind">The entity kind.</param>
    /// <param name="entityId">The entity.</param>
    private static async Task SoftDeleteAsync(World world, AuthoringEntityKind kind, Guid entityId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);

        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            [EntityChange.SoftDelete(kind, entityId)],
            null,
            cancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>The project's stored <c>case_prefix</c> (the insert trigger derived it from the slug).</summary>
    /// <param name="world">The world.</param>
    /// <param name="projectId">The project.</param>
    private Task<string> PrefixAsync(World world, Guid projectId) => _fixture.ScalarAsync<string>(
        world.Database,
        $"select case_prefix from public.project where id = '{projectId:D}'");

    /// <summary>The number of rows one project holds in one table.</summary>
    /// <param name="world">The world.</param>
    /// <param name="table">The table.</param>
    /// <param name="projectId">The project, or the world's.</param>
    private Task<int> CountAsync(World world, string table, Guid? projectId = null) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.{table} where org_id = '{world.Org:D}'"
            + $" and project_id = '{(projectId ?? world.Project):D}'");

    /// <summary>The counter row's <c>next_value</c>, or null when no allocation ran yet.</summary>
    /// <param name="world">The world.</param>
    /// <param name="projectId">The project, or the world's.</param>
    private Task<long?> CounterAsync(World world, Guid? projectId = null) => _fixture.ScalarAsync<long?>(
        world.Database,
        $"select next_value from public.authoring_code_counter where org_id = '{world.Org:D}'"
        + $" and project_id = '{(projectId ?? world.Project):D}'");

    /// <summary>The distinct <c>batch_id</c>s of the project's version rows.</summary>
    /// <param name="world">The world.</param>
    private Task<int> BatchCountAsync(World world) => _fixture.ScalarAsync<int>(
        world.Database,
        $"select count(distinct batch_id)::int from public.authoring_version where org_id = '{world.Org:D}'"
        + $" and project_id = '{world.Project:D}'");

    /// <summary>Every stored <c>content_hash</c> of one entity kind, in version order.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entityKind">The stored entity kind.</param>
    private Task<IReadOnlyList<string>> HashesAsync(World world, string entityKind) => _fixture.ColumnAsync<string>(
        world.Database,
        $"select content_hash from public.authoring_version where org_id = '{world.Org:D}'"
        + $" and project_id = '{world.Project:D}' and entity_kind = '{entityKind}' order by version_no");

    /// <summary>Every stored <c>change_kind</c> of one entity kind, in version order.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entityKind">The stored entity kind.</param>
    private Task<IReadOnlyList<string>> ChangeKindsAsync(World world, string entityKind) => _fixture.ColumnAsync<string>(
        world.Database,
        $"select change_kind from public.authoring_version where org_id = '{world.Org:D}'"
        + $" and project_id = '{world.Project:D}' and entity_kind = '{entityKind}' order by version_no");

    /// <summary>The one live case code of a project whose file carries a single case.</summary>
    /// <param name="world">The world.</param>
    /// <param name="projectId">The project, or the world's.</param>
    private Task<string> CaseCodeAsync(World world, Guid? projectId = null) => _fixture.ScalarAsync<string>(
        world.Database,
        $"select code from public.manual_test_case where org_id = '{world.Org:D}'"
        + $" and project_id = '{(projectId ?? world.Project):D}' and deleted_at is null order by code limit 1");

    /// <summary>
    /// A file of <paramref name="groups"/> independent subtrees, five rows each: AC1's ladder (module, feature, category),
    /// then one checklist item under the category and one case under the item — a category cannot stand at the root and an
    /// item cannot hang under a module (AC1/AC3), so this is the smallest legal subtree.
    /// </summary>
    /// <param name="groups">How many subtrees, i.e. rows / 5.</param>
    private static List<ImportRow> File(int groups)
    {
        var rows = new List<ImportRow>(groups * 5);
        var index = 0;

        for (var group = 1; group <= groups; group++)
        {
            var number = group.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var moduleKey = "m" + number;
            var featureKey = "f" + number;
            var categoryKey = "n" + number;
            var itemKey = "i" + number;

            rows.Add(ImportSample.Node(++index, moduleKey, null, "module", "Module " + number));
            rows.Add(ImportSample.Node(++index, featureKey, moduleKey, "feature", "Feature " + number));
            rows.Add(ImportSample.Node(++index, categoryKey, featureKey, "category", "Category " + number));
            rows.Add(ImportSample.Item(++index, itemKey, categoryKey, "Item " + number));
            rows.Add(ImportSample.Case(++index, "c" + number, itemKey, "Case " + number, ImportSample.Steps));
        }

        return rows;
    }

    /// <summary>The smallest complete file: AC1's ladder and one case, five rows.</summary>
    /// <param name="code">The case code, or null to have one allocated.</param>
    /// <param name="title">The case title.</param>
    /// <param name="state">The case row's state cell, or null for an empty one.</param>
    /// <param name="itemState">The item row's state cell, or null for an empty one.</param>
    /// <param name="extraCases">Further case rows, appended after the first.</param>
    private static IReadOnlyList<ImportRow> Single(
        string? code = null,
        string title = "Case 1.1",
        string? state = null,
        string? itemState = null,
        params ImportRow[] extraCases) =>
    [
        ImportSample.Node(1, "n1", null, "module", "Module 1"),
        ImportSample.Node(2, "n2", "n1", "feature", "Feature 1"),
        ImportSample.Node(3, "n3", "n2", "category", "Category 1"),
        ImportSample.Item(4, "i1", "n3", "Item 1", state: itemState),
        ImportSample.Case(5, "c1", "i1", title, ImportSample.Steps, state: state, code: code),
        .. extraCases,
    ];

    /// <summary>AC13: every version row of one import shares one <c>batch_id</c> and records <c>source = import</c>.</summary>
    [Fact]
    public async Task AtomicApply_OneBatchId()
    {
        await using var world = await StartAsync();

        var result = await ImportAsync(world, world.Project, File(2));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var report = result.Value!;
        report.BatchId.ShouldNotBeNull();
        report.Counts.ShouldBe(new ImportCounts(Created: 10, Updated: 0, Restored: 0, Unchanged: 0));

        (await CountAsync(world, "feature_node")).ShouldBe(6);
        (await CountAsync(world, "checklist_item")).ShouldBe(2);
        (await CountAsync(world, "manual_test_case")).ShouldBe(2);
        (await CountAsync(world, "authoring_version")).ShouldBe(10);
        (await BatchCountAsync(world)).ShouldBe(1);

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.authoring_version where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' and batch_id = '{report.BatchId:D}' and source = 'import'")
).ShouldBe(10);

        // One allocation block for the two code-less cases, so the counter stands just past it.
        (await CounterAsync(world)).ShouldBe(3L);
    }

    /// <summary>AC13: one invalid row — the last of 201 — refuses the file and leaves every table as it was.</summary>
    [Fact]
    public async Task InvalidRow_WritesNothing()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world, world.Project);

        var rows = new List<ImportRow>(File(40));

        // The last row names an item that is not in the file: an orphan, met only after 200 good rows were validated.
        rows.Add(ImportSample.Case(
            rows.Count + 1,
            "c-last",
            "missing-item",
            "the last row is wrong",
            ImportSample.Steps,
            code: prefix + "-900"));

        var result = await ImportAsync(world, world.Project, rows);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(422);
        result.Error.Details.ShouldNotBeNull();
        result.Error.Details.ShouldContain(detail => detail.Path == "/item_key" && detail.Reason == "unknown_parent" && detail.Index == 201);

        foreach (var table in new[] { "feature_node", "checklist_item", "manual_test_case", "authoring_version" })
        {
            (await CountAsync(world, table)).ShouldBe(0, table + " must stay empty");
        }

        (await CounterAsync(world)).ShouldBeNull("a refused file allocates nothing");
    }

    /// <summary>AC13: a dry run answers the same report and neither writes a row nor touches the counter.</summary>
    [Fact]
    public async Task DryRun_ReturnsReportWritesNothing()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world, world.Project);

        // A mixed file: the first case carries an explicit code, the other two ask for one.
        var rows = Single(prefix + "-010");

        var result = await ImportAsync(world, world.Project, rows, dryRun: true);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var report = result.Value!;
        report.BatchId.ShouldBeNull();
        report.Counts.Created.ShouldBe(5);
        report.Rows.Count.ShouldBe(5);
        report.Rows.ShouldAllBe(row => row.Outcome == "create");
        report.Rows.Single(row => row.Kind == ImportRowKind.Case).Code.ShouldBe(prefix + "-010");

        // The same file with no code reports a case with no code: a dry run never allocates one.
        var codeLess = (await ImportAsync(world, world.Project, Single(), dryRun: true));
        codeLess.IsSuccess.ShouldBeTrue(codeLess.Error?.Message);
        codeLess.Value!.Rows.Single(row => row.Kind == ImportRowKind.Case).Code.ShouldBeNull();

        foreach (var table in new[] { "feature_node", "checklist_item", "manual_test_case", "authoring_version" })
        {
            (await CountAsync(world, table)).ShouldBe(0, table + " must stay empty");
        }

        (await CounterAsync(world)).ShouldBeNull("a dry run touches no counter");
    }

    /// <summary>
    /// AC15: an exported file re-imported unchanged appends nothing; a changed case updates; a soft-deleted code restores
    /// with <c>change_kind = restore</c>.
    /// </summary>
    [Fact]
    public async Task Reimport_IdempotentByCode()
    {
        await using var world = await StartAsync();

        var first = await ImportAsync(world, world.Project, Single());
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);

        var code = await CaseCodeAsync(world);
        var versionsAfterFirst = await CountAsync(world, "authoring_version");
        versionsAfterFirst.ShouldBe(5);

        // 1. The same file, now carrying the code the import allocated: nothing to do.
        var unchanged = await ImportAsync(world, world.Project, Single(code));
        unchanged.IsSuccess.ShouldBeTrue(unchanged.Error?.Message);
        unchanged.Value!.BatchId.ShouldBeNull();
        unchanged.Value.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 0, Restored: 0, Unchanged: 5));
        (await CountAsync(world, "authoring_version")).ShouldBe(versionsAfterFirst);

        // 2. A changed title is one update row for the case and nothing for its parent rows.
        var updated = await ImportAsync(world, world.Project, Single(code, "Case 1.1 retitled"));
        updated.IsSuccess.ShouldBeTrue(updated.Error?.Message);
        updated.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 1, Restored: 0, Unchanged: 4));
        (await CountAsync(world, "authoring_version")).ShouldBe(versionsAfterFirst + 1);

        // 3. A soft-deleted code comes back through change_kind = restore, and the row is live again.
        var caseId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' and code = '{code}'");
        await SoftDeleteAsync(world, AuthoringEntityKind.ManualTestCase, caseId);

        var restored = await ImportAsync(world, world.Project, Single(code, "Case 1.1 retitled"));
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);
        restored.Value!.BatchId.ShouldNotBeNull();
        restored.Value.Counts.Restored.ShouldBe(1);
        restored.Value.Rows.Single(row => row.Kind == ImportRowKind.Case).Outcome.ShouldBe("restore");

        (await ChangeKindsAsync(world, "manual_test_case"))
            .ShouldBe(["create", "update", "delete", "restore"]);

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select deleted_at is null from public.manual_test_case where id = '{caseId:D}'")
).ShouldBeTrue();
    }

    /// <summary>
    /// AC15: a matched item whose text differs is one <c>update</c> — and the node and case rows of the same file stay
    /// <c>unchanged</c>, which is what keeps a case snapshot neither cast to an item row nor written twice.
    /// </summary>
    [Fact]
    public async Task Reimport_ChangedItemDescription_Updates()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var code = await CaseCodeAsync(world);
        var versions = await CountAsync(world, "authoring_version");

        var rows = Single(code)
            .Select(row => row.Kind == ImportRowKind.Item ? row with { Description = "a new description" } : row)
            .ToArray();

        var result = await ImportAsync(world, world.Project, rows);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 1, Restored: 0, Unchanged: 4));
        result.Value.Rows.Single(row => row.Kind == ImportRowKind.Item).Outcome.ShouldBe("update");
        (await CountAsync(world, "authoring_version")).ShouldBe(versions + 1);

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select description from public.checklist_item where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).ShouldBe("a new description");
    }

    /// <summary>
    /// AC13's state-machine guard: a retired item is terminal for the routes' update paths, so a file that would rewrite
    /// it is refused instead of bypassing that check; the row as stored still round-trips as <c>unchanged</c>.
    /// </summary>
    [Fact]
    public async Task RetiredRow_CannotBeRewritten()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var code = await CaseCodeAsync(world);

        // The item's own node: with AC1's ladder the project has three nodes, and only the item's category parents it.
        var nodeId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select feature_node_id from public.checklist_item where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")
;
        // The item's own node: with the AC1 ladder the project has three nodes, and only the item's category parents it.
        var itemId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.checklist_item where org_id = '{world.Org:D}' and project_id = '{world.Project:D}'")
;

        await RetireItemAsync(world, itemId, nodeId);
        var versions = await CountAsync(world, "authoring_version");

        var changed = Single(code)
            .Select(row => row.Kind == ImportRowKind.Item ? row with { Description = "rewritten" } : row)
            .ToArray();

        var refused = await ImportAsync(world, world.Project, changed);

        refused.IsSuccess.ShouldBeFalse("a retired item may not be rewritten by an import");
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Details.ShouldNotBeNull();
        refused.Error.Details.ShouldContain(detail => detail.Path == "/state" && detail.Reason == "value_invalid" && detail.Index == 4);
        (await CountAsync(world, "authoring_version")).ShouldBe(versions);

        // The row exactly as stored — retired, same text — re-imports as unchanged, so a round trip never trips the guard.
        var same = Single(code)
            .Select(row => row.Kind == ImportRowKind.Item ? row with { State = ChecklistItemStateMachine.Retired } : row)
            .ToArray();

        var accepted = await ImportAsync(world, world.Project, same);

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 0, Restored: 0, Unchanged: 5));
    }

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
        await using var world = await StartAsync();
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
        await using var world = await StartAsync();

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

    /// <summary>Moves the imported item to its terminal state through the writer, the one path that may change a state.</summary>
    /// <param name="world">The world.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="nodeId">Its owning node.</param>
    private static async Task RetireItemAsync(World world, Guid itemId, Guid nodeId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);

        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var retired = new ChecklistItemSnapshot(
            nodeId,
            "Item 1",
            null,
            "high",
            ChecklistItemStateMachine.Retired,
            0);

        using var document = JsonDocument.Parse(AuthoringContent.Serialize(retired));

        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            [EntityChange.Update(AuthoringEntityKind.ChecklistItem, itemId, document.RootElement.Clone())],
            // A move into `retired` without a reason is 422 /reason (AuthoringStates.cs:188, ReasonRequired), so the
            // retire this test performs must carry one, exactly like ChecklistItemUpdate.cs:144 does.
            new AuthoringAppendContext { Reason = "retired by the importer test" },
            cancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Code F1 (N2a): an empty <c>state</c> cell keeps the stored state. The case is approved through the writer, then the
    /// file is re-imported with one edited title: the title lands, the state stays approved, and the item row is unchanged.
    /// </summary>
    [Fact]
    public async Task MatchedState_EmptyCell_KeepsTheApprovedCaseApproved()
    {
        await using var world = await StartAsync();

        var created = await ImportAsync(world, world.Project, Single());
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var (caseId, itemId, code) = await CaseIdentityAsync(world);
        await AppendCaseAsync(world, caseId, itemId, code, "approved", "Case 1.1");

        // Matched by code: the key is unique per file, the code is what identifies the stored case (AC4).
        var edited = await ImportAsync(world, world.Project, Single(code: code, title: "Case 1.1 edited"));

        edited.IsSuccess.ShouldBeTrue(edited.Error?.Message);
        edited.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 1, Restored: 0, Unchanged: 4));
        (await CaseStatesAsync(world)).ShouldBe(["approved"]);
        (await CaseTitlesAsync(world)).ShouldBe(["Case 1.1 edited"]);
    }

    /// <summary>
    /// Code F1 (N2b): a <c>state</c> cell is only taken when the same state machine the route uses allows the move. A move
    /// into <c>retired</c> without a reason and a value the item's state machine does not know are refused on
    /// <c>/state</c> as <c>value_invalid</c>, and no version row is written.
    /// </summary>
    [Fact]
    public async Task MatchedState_IllegalTransitionAndReasonlessRetire_Refused()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var (_, _, code) = await CaseIdentityAsync(world);
        var versions = await VersionCountAsync(world);

        // `retired` is a legal case state but the move needs a reason, which the row table cannot carry (Code F1/F3). Index 5
        // is the case row, so this cannot pass on the item refusal below.
        var retire = await ImportAsync(world, world.Project, Single(code: code, state: "retired"));

        retire.IsSuccess.ShouldBeFalse();
        retire.Error!.Details.ShouldNotBeNull();
        retire.Error.Details.ShouldContain(
            detail => detail.Index == 5 && detail.Path == "/state" && detail.Reason == "value_invalid");

        // The item's own reasonless retire: `retired` is a state the schema accepts (so the row reaches the state machine,
        // unlike a value outside the enum), and the machine still refuses the move for want of a reason. Index 4.
        var illegal = await ImportAsync(world, world.Project, Single(code: code, itemState: "retired"));

        illegal.IsSuccess.ShouldBeFalse();
        illegal.Error!.Details.ShouldNotBeNull();
        illegal.Error.Details.ShouldContain(
            detail => detail.Index == 4 && detail.Path == "/state" && detail.Reason == "value_invalid");

        (await VersionCountAsync(world)).ShouldBe(versions, "a refused row writes nothing");
    }

    /// <summary>
    /// Code F3 (N2c): restoring a soft-deleted case always writes <see cref="ManualTestCaseStateMachine.RestoreState"/> —
    /// draft — whatever the file's <c>state</c> cell says.
    /// </summary>
    [Fact]
    public async Task Restore_ForcesDraft_WhateverTheFileSays()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();

        var (caseId, itemId, code) = await CaseIdentityAsync(world);
        await AppendCaseAsync(world, caseId, itemId, code, "approved", "Case 1.1");
        await SoftDeleteAsync(world, AuthoringEntityKind.ManualTestCase, caseId);

        var restored = await ImportAsync(world, world.Project, Single(code: code, state: "approved"));

        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);
        restored.Value!.Rows.Single(row => row.Kind == ImportRowKind.Case).Outcome.ShouldBe("restore");
        (await CaseStatesAsync(world)).ShouldBe([ManualTestCaseStateMachine.Draft]);
    }

    /// <summary>
    /// Findings E and N1 (N2d/N2e): an item stored as <c>retired</c> still re-imports unchanged (the export round trip must
    /// survive), and no case may be created under it.
    /// </summary>
    [Fact]
    public async Task StoredRetiredItem_UnchangedRowImports_NewCaseUnderItRefused()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var (_, itemId, code) = await CaseIdentityAsync(world);
        var nodeId = (await _fixture.ColumnAsync<Guid>(
            world.Database,
            $"select feature_node_id from public.checklist_item where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).Single();

        await RetireItemAsync(world, itemId, nodeId);

        // The export of that project, re-imported as-is: the retired item row is unchanged and permitted.
        var roundTrip = await ImportAsync(world, world.Project, Single(code: code));

        roundTrip.IsSuccess.ShouldBeTrue(roundTrip.Error?.Message);
        roundTrip.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 0, Restored: 0, Unchanged: 5));

        // A new case under the retired item is not: the writer refuses it, so the file must too.
        var refused = await ImportAsync(
            world,
            world.Project,
            Single(code: code, extraCases: [ImportSample.Case(6, "c2", "i1", "Case 1.2", ImportSample.Steps)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldNotBeNull();
        refused.Error.Details.ShouldContain(detail => detail.Path == "/item_key" && detail.Reason == "value_invalid");
        (await CaseStatesAsync(world)).Count.ShouldBe(1, "the refused row wrote nothing");
    }

    /// <summary>
    /// Decision N1 (N2f): a new row takes the file's <c>state</c> as written, so an export-shaped file that creates a
    /// retired item and an approved case imports as-is and a second import finds every row unchanged.
    /// </summary>
    [Fact]
    public async Task CreatedRows_TakeTheFileState_SoTheExportShapeRoundTrips()
    {
        await using var world = await StartAsync();

        // The export shape: the code is written, so the second import matches the stored case (the key is file-local).
        var code = (await PrefixAsync(world, world.Project)) + "-001";

        IReadOnlyList<ImportRow> file =
        [
            ImportSample.Node(1, "n1", null, "module", "Module 1"),
            ImportSample.Node(2, "n2", "n1", "feature", "Feature 1"),
            ImportSample.Node(3, "n3", "n2", "category", "Category 1"),
            ImportSample.Item(4, "i1", "n3", "Item 1", state: ChecklistItemStateMachine.Retired),
            ImportSample.Case(
                5, "c1", "i1", "Case 1.1", ImportSample.Steps, state: ManualTestCaseStateMachine.Approved, code: code),
        ];

        var created = await ImportAsync(world, world.Project, file);

        created.IsSuccess.ShouldBeTrue(
            created.Error?.Message + " | " + string.Join(
                "; ",
                (created.Error?.Details ?? []).Select(detail => $"{detail.Index}{detail.Path}:{detail.Reason}")));
        created.Value!.Counts.ShouldBe(new ImportCounts(Created: 5, Updated: 0, Restored: 0, Unchanged: 0));

        var itemState = (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select state from public.checklist_item where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).Single();

        itemState.ShouldBe(ChecklistItemStateMachine.Retired);
        (await CaseStatesAsync(world)).ShouldBe([ManualTestCaseStateMachine.Approved]);

        var versions = await VersionCountAsync(world);
        var again = await ImportAsync(world, world.Project, file);

        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        again.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 0, Restored: 0, Unchanged: 5));
        (await VersionCountAsync(world)).ShouldBe(versions, "an unchanged re-import appends nothing");
    }

    /// <summary>The case's id, its parent item's id and its code, from the one case the fixture creates.</summary>
    /// <param name="world">The world.</param>
    private async Task<(Guid CaseId, Guid ItemId, string Code)> CaseIdentityAsync(World world)
    {
        var rows = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select id::text || '|' || checklist_item_id::text || '|' || code from public.manual_test_case"
            + $" where org_id = '{world.Org:D}' and project_id = '{world.Project:D}'");

        var parts = rows.Single().Split('|');

        return (Guid.Parse(parts[0]), Guid.Parse(parts[1]), parts[2]);
    }

    /// <summary>Moves one case to a state through the writer, the one path that may change it.</summary>
    /// <param name="world">The world.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="itemId">Its parent item.</param>
    /// <param name="code">Its stored code.</param>
    /// <param name="state">The state to move to.</param>
    /// <param name="title">The title to store with it.</param>
    private static async Task AppendCaseAsync(
        World world,
        Guid caseId,
        Guid itemId,
        string code,
        string state,
        string title)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);

        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var approved = new ManualTestCaseSnapshot(itemId, code, title, null, null, ImportSample.Steps, "medium", state);

        using var document = JsonDocument.Parse(AuthoringContent.Serialize(approved));

        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            [EntityChange.Update(AuthoringEntityKind.ManualTestCase, caseId, document.RootElement.Clone())],
            null,
            cancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>Every stored case state of the project.</summary>
    /// <param name="world">The world.</param>
    private async Task<IReadOnlyList<string>> CaseStatesAsync(World world) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"select state from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' order by code");

    /// <summary>Every stored case title of the project.</summary>
    /// <param name="world">The world.</param>
    private async Task<IReadOnlyList<string>> CaseTitlesAsync(World world) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"select title from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' order by code");

    /// <summary>The project's <c>authoring_version</c> row count.</summary>
    /// <param name="world">The world.</param>
    private async Task<int> VersionCountAsync(World world) =>
        (await _fixture.ColumnAsync<int>(
            world.Database,
            $"select count(*)::int from public.authoring_version where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).Single();

    /// <summary>Runs one import with the host's statement log cleared, and answers how many statements it sent.</summary>
    /// <param name="world">The world.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The rows.</param>
    private static async Task<int> StatementsAsync(World world, Guid projectId, IReadOnlyList<ImportRow> rows)
    {
        world.Api.Statements.Clear();
        var result = await ImportAsync(world, projectId, rows);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return world.Api.Statements.Snapshot().Count;
    }
}

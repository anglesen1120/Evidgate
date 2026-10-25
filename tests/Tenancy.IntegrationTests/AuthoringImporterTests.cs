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
[Collection(TenancySuite.Name)]
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
        Guid Project) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
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
    private static IReadOnlyList<ImportRow> Single(string? code = null, string title = "Case 1.1") =>
    [
        ImportSample.Node(1, "n1", null, "module", "Module 1"),
        ImportSample.Node(2, "n2", "n1", "feature", "Feature 1"),
        ImportSample.Node(3, "n3", "n2", "category", "Category 1"),
        ImportSample.Item(4, "i1", "n3", "Item 1"),
        ImportSample.Case(5, "c1", "i1", title, ImportSample.Steps, code: code),
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

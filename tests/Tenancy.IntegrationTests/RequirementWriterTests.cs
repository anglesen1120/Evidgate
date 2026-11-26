using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Authoring.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The requirement head writer against a real PostgreSQL: identity and tree, body cap after NFC, append-only history
/// with restore, the one-winner head, and refusals that leave nothing behind.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RequirementWriterTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RequirementWriterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static string CodePoints(string unit, int count) => new StringBuilder(unit.Length * count).Insert(0, unit, count).ToString();

    private Task<long> CountAsync(RequirementTestWorld world, string table, string where = "true") =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{table} where org_id = '{world.Org:D}' and {where}");

    [Fact]
    public async Task Create_StoresTheTreeWithoutAConnector_AndReadsTheHead()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        var epic = await world.CreatedAsync(world.Create("epic", "Checkout"));
        var story = await world.CreatedAsync(world.Create("user_story", "Pay by card", parent: epic.RequirementId, externalRef: "SHOP-12"));
        var first = await world.CreatedAsync(world.Create("acceptance_criterion", "Card accepted", parent: story.RequirementId));
        await world.CreatedAsync(world.Create("acceptance_criterion", "Card declined", parent: story.RequirementId));
        var section = await world.CreatedAsync(world.Create("spec_section", "Appendix", featureNode: null));
        await world.CreatedAsync(world.Create("other", "Note"));

        (await CountAsync(world, "requirement", $"project_id = '{world.Project:D}'")).ShouldBe(6);
        (await CountAsync(world, "requirement", $"parent_requirement_id = '{story.RequirementId:D}'")).ShouldBe(2);

        var read = await world.InScopeAsync((writer, token) => writer.GetAsync(world.Org, world.Project, first.RequirementId, token), commit: false);
        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.Value.Id.ShouldBe(first.RequirementId);
        read.Value.HeadVersionNo.ShouldBe(1);
        read.Value.HeadVersionId.ShouldBe(first.VersionId);
        read.Value.ParentRequirementId.ShouldBe(story.RequirementId);
        section.VersionNo.ShouldBe(1);
    }

    [Fact]
    public async Task Create_RefusesInvalidKindRefParentAndFeatureNode_AndWritesNothing()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var foreignParent = await world.CreatedAsync(world.Create(project: world.OtherProject));
        var foreignNode = Guid.CreateVersion7();
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await seed.ExecuteAsync(
                "insert into public.feature_node (id, org_id, project_id, kind, title, position) values (@id, @org_id, @project_id, 'module', 'Shop', 0)",
                ("id", foreignNode),
                ("org_id", world.Org),
                ("project_id", world.OtherProject));
            await seed.CommitAsync();
        }

        var before = await CountAsync(world, "requirement");
        var cases = new (RequirementCreate Create, string Path)[]
        {
            (world.Create("feature"), "/kind"),
            (world.Create(externalRef: "has space"), "/external_ref"),
            (world.Create(externalRef: new string('a', 129)), "/external_ref"),
            (world.Create(title: " "), "/title"),
            (world.Create(title: new string('t', 201)), "/title"),
            (world.Create(parent: foreignParent.RequirementId), "/parent_requirement_id"),
            (world.Create(parent: Guid.CreateVersion7()), "/parent_requirement_id"),
            (world.Create(featureNode: foreignNode), "/feature_node_id"),
        };

        foreach (var (create, path) in cases)
        {
            var result = await world.InScopeAsync((writer, token) => writer.CreateAsync(world.Org, create, token), commit: true);

            result.IsSuccess.ShouldBeFalse(path);
            result.Error!.Status.ShouldBe(422, path);
            result.Error.Details!.ShouldContain(detail => detail.Path == path, path);
        }

        (await CountAsync(world, "requirement")).ShouldBe(before);
        (await CountAsync(world, "requirement_version")).ShouldBe(1);
    }

    [Fact]
    public async Task Body_AcceptsExactlyTwentyThousandNfcCodePoints_IncludingNonBmpText()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        var astral = await world.CreatedAsync(world.Create(body: CodePoints("\U0001F600", 20_000)));
        var tooLong = await world.InScopeAsync(
            (writer, token) => writer.CreateAsync(world.Org, world.Create(body: CodePoints("\U0001F600", 20_001)), token),
            commit: true);

        astral.VersionNo.ShouldBe(1);
        tooLong.IsSuccess.ShouldBeFalse();
        tooLong.Error!.Details!.ShouldContain(detail => detail.Path == "/body");
    }

    [Fact]
    public async Task Body_IsNormalizedToNfcBeforeTheCapIsChecked_AndStoredComposed()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);

        // 40 000 raw code points that compose to exactly 20 000, then one more composed character.
        var atCap = await world.CreatedAsync(world.Create(body: CodePoints("e\u0301", 20_000)));
        var overCap = await world.InScopeAsync(
            (writer, token) => writer.CreateAsync(world.Org, world.Create(body: CodePoints("e\u0301", 20_000) + "x"), token),
            commit: true);

        overCap.IsSuccess.ShouldBeFalse();
        var stored = await _fixture.ScalarAsync<string>(world.Database, $"select body from public.requirement_version where id = '{atCap.VersionId:D}'");
        stored.ShouldBe(CodePoints("\u00e9", 20_000));
        (await _fixture.ScalarAsync<long>(world.Database, $"select char_length(body) from public.requirement_version where id = '{atCap.VersionId:D}'")).ShouldBe(20_000);

        // A decomposed and a composed body are one content hash.
        var composed = await world.CreatedAsync(world.Create(body: "caf\u00e9"));
        var decomposed = await world.CreatedAsync(world.Create(body: "cafe\u0301"));
        decomposed.ContentHash.ShouldBe(composed.ContentHash);
    }

    [Fact]
    public async Task History_KeepsEveryVersionAndRestoreAppends_LeavingEarlierRowsUnchanged()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var author = await world.MembershipAsync();

        var created = await world.InScopeAsync((writer, token) => writer.CreateAsync(world.Org, world.Create(body: "first") with { AuthorMembershipId = author, Reason = "initial" }, token));
        var edited = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, created.Value.RequirementId, created.Value.RowVersion, "second", "import", author, "reworded", token));
        edited.IsSuccess.ShouldBeTrue(edited.Error?.Message);

        var rowsBefore = await _fixture.ScalarAsync<string>(world.Database, $"select string_agg(v::text, '|' order by version_no) from public.requirement_version v where requirement_id = '{created.Value.RequirementId:D}'");

        var restored = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, created.Value.RequirementId, edited.Value.RowVersion, "first", "manual", author, "undo", token));
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);
        restored.Value.VersionNo.ShouldBe(3);
        restored.Value.ContentHash.ShouldBe(created.Value.ContentHash, "same body, same hash, still a new row");
        restored.Value.VersionId.ShouldNotBe(created.Value.VersionId);

        var history = await world.InScopeAsync((writer, token) => writer.ListVersionsAsync(world.Org, world.Project, created.Value.RequirementId, null, 10, token), commit: false);
        history.Value.Select(row => row.VersionNo).ShouldBe([3, 2, 1]);
        history.Value.Select(row => row.Source).ShouldBe(["manual", "import", "manual"]);
        history.Value.Select(row => row.Reason).ShouldBe(["undo", "reworded", "initial"]);
        history.Value.ShouldAllBe(row => row.AuthorMembershipId == author);

        var older = await world.InScopeAsync((writer, token) => writer.ListVersionsAsync(world.Org, world.Project, created.Value.RequirementId, 3, 1, token), commit: false);
        older.Value.Select(row => row.VersionNo).ShouldBe([2]);

        var rowsAfter = await _fixture.ScalarAsync<string>(world.Database, $"select string_agg(v::text, '|' order by version_no) from public.requirement_version v where requirement_id = '{created.Value.RequirementId:D}' and version_no <= 2");
        rowsAfter.ShouldBe(rowsBefore);
        (await _fixture.ScalarAsync<int>(world.Database, $"select head_version_no from public.requirement where id = '{created.Value.RequirementId:D}'")).ShouldBe(3);
    }

    [Fact]
    public async Task Append_WithAStaleRowVersion_Is412AndAppendsNothing()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        var first = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, head.RequirementId, head.RowVersion, "one", "manual", null, null, token));
        var stale = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, head.RequirementId, head.RowVersion, "two", "manual", null, null, token), commit: true);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        stale.IsSuccess.ShouldBeFalse();
        stale.Error!.Status.ShouldBe(412);
        stale.Error.Code.ShouldBe(ApiErrorCode.PreconditionFailed);
        (await CountAsync(world, "requirement_version", $"requirement_id = '{head.RequirementId:D}'")).ShouldBe(2);
    }

    [Fact]
    public async Task Append_TwoWritersOnOneRowVersion_OneWinsAndNumbersStayUnique()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var cancellationToken = TestContext.Current.CancellationToken;

        // Writer A holds the row lock (uncommitted); writer B starts on the same token and must wait for it.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerA = world.InScopeAsync(
            async (writer, token) =>
            {
                var result = await writer.AppendVersionAsync(world.Org, world.Project, head.RequirementId, head.RowVersion, "from A", "manual", null, null, token);
                gate.SetResult();
                await release.Task.WaitAsync(token);

                return result;
            });
        await gate.Task.WaitAsync(cancellationToken);
        var writerB = world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, head.RequirementId, head.RowVersion, "from B", "manual", null, null, token));
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        writerB.IsCompleted.ShouldBeFalse("B must wait on A's row lock");
        release.SetResult();

        var results = await Task.WhenAll(writerA, writerB);

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Single(result => !result.IsSuccess).Error!.Status.ShouldBe(412);
        var numbers = await _fixture.ColumnAsync<int>(world.Database, $"select version_no from public.requirement_version where requirement_id = '{head.RequirementId:D}' order by 1");
        numbers.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Append_RolledBack_MovesNeitherTheHeadNorTheHistory()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());

        var appended = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, world.Project, head.RequirementId, head.RowVersion, "never committed", "manual", null, null, token), commit: false);

        appended.IsSuccess.ShouldBeTrue(appended.Error?.Message);
        (await _fixture.ScalarAsync<int>(world.Database, $"select head_version_no from public.requirement where id = '{head.RequirementId:D}'")).ShouldBe(1);
        (await CountAsync(world, "requirement_version", $"requirement_id = '{head.RequirementId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Append_RefusesBadFieldsMissingForeignAndDeletedRequirements_WithoutAWrite()
    {
        await using var world = await RequirementTestWorld.StartAsync(_fixture);
        var head = await world.CreatedAsync(world.Create());
        var deleted = await world.CreatedAsync(world.Create(title: "Gone"));
        await _fixture.ExecuteAsync(world.Database, $"update public.requirement set deleted_at = now() where id = '{deleted.RequirementId:D}'");
        var rowVersion = await _fixture.ScalarAsync<int>(world.Database, $"select version from public.requirement where id = '{deleted.RequirementId:D}'");

        var cases = new (Guid Project, Guid Id, int Version, string Body, string Source, string? Reason, int Status)[]
        {
            (world.Project, head.RequirementId, head.RowVersion, "x", "bot", null, 422),
            (world.Project, head.RequirementId, head.RowVersion, "x", "manual", new string('r', 501), 422),
            (world.Project, head.RequirementId, head.RowVersion, "x", "manual", "", 422),
            (world.Project, head.RequirementId, head.RowVersion, CodePoints("a", 20_001), "manual", null, 422),
            (world.Project, Guid.CreateVersion7(), 1, "x", "manual", null, 404),
            (world.OtherProject, head.RequirementId, head.RowVersion, "x", "manual", null, 404),
            (world.Project, deleted.RequirementId, rowVersion, "x", "manual", null, 404),
        };

        foreach (var (project, id, version, body, source, reason, status) in cases)
        {
            var result = await world.InScopeAsync((writer, token) => writer.AppendVersionAsync(world.Org, project, id, version, body, source, null, reason, token), project, commit: true);

            result.IsSuccess.ShouldBeFalse(source + reason);
            result.Error!.Status.ShouldBe(status, source + reason);
        }

        (await CountAsync(world, "requirement_version", $"requirement_id in ('{head.RequirementId:D}', '{deleted.RequirementId:D}')")).ShouldBe(2);
    }
}

// What a batch refuses and how it names the refusal: every entry rule answers one 422 detail by entry index, the
// in-process contract throws instead of writing, a project the org cannot see is a refused entry rather than a
// foreign-key abort, and the caps count characters the way the column does.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Keeps batch and entry refusal boundaries together across the command and in-process paths.</summary>
public sealed partial class UsageRecordBatchTests
{
    // ------------------------------------------------------------------ 422: every entry rule names its index

    [Fact]
    public async Task RecordUsageBatch_InvalidEntries_422WithPerEntryIndices()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var refused = await SendAsync(
            world,
            Batch(
                "usage-invalid",
                [
                    Entry("batch:1:results", world.Project, metric: (UsageMetric)42),
                    Entry("batch:2:results", world.Project, period: "2026-13"),
                    Entry(" ", world.Project),
                    Entry(new string('k', IUsageLedger.MaxBillingKeyLength + 1), world.Project),
                    Entry("batch:5:results", world.Project, unit: " "),
                ]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Code.ShouldBe("request_invalid");

        var details = refused.Error.Details!;
        details.Select(detail => (detail.Index, detail.Path, detail.Reason)).ShouldBe(
            [
                ((int?)0, "/entries/0/metric", "invalid_value"),
                (1, "/entries/1/period", "invalid_value"),
                (2, "/entries/2/billing_key", "required"),
                (3, "/entries/3/billing_key", "too_long"),
                (4, "/entries/4/unit", "required"),
            ],
            "one 00 §9.2 detail per offending entry, indexed by position");

        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "a refused batch writes nothing");
        LedgerStatements(world).ShouldBeEmpty("validation runs before the statement");
    }

    [Fact]
    public async Task RecordUsageBatch_NoEntriesMember_422OnTheBatch()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(world, new RecordUsageBatch(null!, "usage-null-entries"));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Path.ShouldBe("/entries");
        detail.Reason.ShouldBe("required");
        detail.Index.ShouldBeNull("the missing member belongs to the batch, not to an entry");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task RecordUsageBatch_BatchCap_1000Accepted_1001Refused()
    {
        await using var world = await StartAsync();

        var overCap = Enumerable.Range(0, IUsageLedger.MaxBatchSize + 1)
            .Select(index => Entry($"batch:{index}:results", world.Project))
            .ToList();
        var refused = await SendAsync(world, Batch("usage-over-cap", overCap));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Path.ShouldBe("/entries");
        detail.Reason.ShouldBe("too_many");
        detail.Index.ShouldBeNull();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        var atCap = Enumerable.Range(0, IUsageLedger.MaxBatchSize)
            .Select(index => Entry($"batch:{index}:results", world.Project))
            .ToList();
        var accepted = await SendAsync(world, Batch("usage-at-cap", atCap));

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value!.Inserted.Count.ShouldBe(IUsageLedger.MaxBatchSize);
        accepted.Value.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(IUsageLedger.MaxBatchSize);
    }

    [Fact]
    public async Task RecordUsageBatch_BillingKeyLength_CountsCharactersNotUtf16Units()
    {
        await using var world = await StartAsync();

        // 200 emoji: 400 UTF-16 units but 200 characters — the unit the 1..200 column contract and PostgreSQL's
        // char_length use, and the reason the rule counts runes instead of `string.Length`.
        var atLimit = string.Concat(Enumerable.Repeat("\U0001F3AF", IUsageLedger.MaxBillingKeyLength));
        atLimit.Length.ShouldBe(IUsageLedger.MaxBillingKeyLength * 2, "the fixture key must be multibyte to prove the rule");

        var accepted = await RecordAsync(
            world,
            [Entry(atLimit, world.Project)],
            cancellationToken: TestContext.Current.CancellationToken);
        accepted.Inserted.ShouldBe([atLimit]);
        (await ColumnAsync(world, world.Org, atLimit, "char_length(billing_key)::text")).ShouldBe("200");

        var overLimit = atLimit + "\U0001F3AF";
        var refused = await SendAsync(world, Batch("usage-multibyte", [Entry(overLimit, world.Project)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(0);
        detail.Path.ShouldBe("/entries/0/billing_key");
        detail.Reason.ShouldBe("too_long");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "a refused batch writes nothing");
    }

    [Fact]
    public async Task RecordUsageBatch_EmptyBatch_IsANoOp()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var empty = await SendAsync(world, Batch("usage-empty", []));

        empty.IsSuccess.ShouldBeTrue(empty.Error?.Message);
        empty.Value!.Inserted.ShouldBeEmpty();
        empty.Value.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
        LedgerStatements(world).ShouldBeEmpty("an empty batch writes nothing");

        // The in-process path answers the same shape without opening a statement either.
        var contractEmpty = await RecordAsync(
            world,
            [],
            cancellationToken: TestContext.Current.CancellationToken);
        contractEmpty.Inserted.ShouldBeEmpty();
        contractEmpty.Duplicates.ShouldBeEmpty();
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ the in-process contract's own edge

    [Fact]
    public async Task RecordAsync_InvalidEntry_ThrowsBeforeAnyStatement()
    {
        await using var world = await StartAsync();

        world.Commands.Statements.Clear();
        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await RecordAsync(world, [Entry("batch:1:results", world.Project, period: "2026-13")]));

        exception.Message.ShouldContain("/entries/0/period");
        exception.Message.ShouldContain("invalid_value");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);
        LedgerStatements(world).ShouldBeEmpty("an in-process caller's bug never reaches the database");
    }

    // ------------------------------------------------------------------ projects: refused entry, never a 23503

    [Fact]
    public async Task RecordUsageBatch_UnknownProject_422AtItsEntry()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        world.Commands.Statements.Clear();
        var refused = await SendAsync(
            world,
            Batch(
                "usage-unknown-project",
                [Entry("batch:1:results", world.Project), Entry("batch:2:results", unknown)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(1);
        detail.Path.ShouldBe("/entries/1/project_id");
        detail.Reason.ShouldBe("invalid_value");
        (await LedgerCountAsync(world, world.Org))
            .ShouldBe(0, "a refused batch writes nothing, not even the entries that were valid");
        LedgerStatements(world).ShouldBeEmpty("the project check runs before the insert");
    }

    [Fact]
    public async Task RecordUsageBatch_ForeignProject_RefusedAndCallerTransactionStaysUsable()
    {
        await using var world = await StartAsync();

        // Another organization's project, made by its own user: the id is real, and row security hides it from this org.
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("globex"));
        var foreignProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");

        // The service path: the foreign project is invisible under RLS, so it is a refused entry, not a 23503 that the
        // pipeline would answer as a server error.
        var refused = await SendAsync(
            world,
            Batch("usage-foreign-project", [Entry("batch:1:results", foreignProject)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Details!.ShouldHaveSingleItem().Path.ShouldBe("/entries/0/project_id");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        // The in-process path: the refusal must leave the caller's transaction usable — it is mid-work and a 25P02
        // would break everything it still has to write.
        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);
        var ledger = provider.GetRequiredService<IUsageLedger>();

        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await ledger.RecordAsync(new RecordUsageBatchRequest([Entry("batch:1:results", foreignProject)]), token));
        exception.Message.ShouldContain("/entries/0/project_id");
        exception.Message.ShouldContain("invalid_value");

        var accepted = await ledger.RecordAsync(
            new RecordUsageBatchRequest([Entry("batch:2:results", world.Project)]), token);
        accepted.Inserted.ShouldBe(["batch:2:results"], "a refused entry leaves the transaction usable");
        await scope.CommitAsync(token);

        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "only the entry that named the caller's own project landed");
    }

    // ------------------------------------------------------------------ entry rules: quantity, unit, storable text

    [Fact]
    public async Task RecordUsageBatch_NegativeQuantity_422()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(
            world,
            Batch("usage-negative", [Entry("batch:1:results", world.Project, -1m)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(0);
        detail.Path.ShouldBe("/entries/0/quantity");
        detail.Reason.ShouldBe("invalid_value");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        // The in-process path applies the same rule; a correction is an adjustment row, never a negative plain row.
        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await RecordAsync(world, [Entry("batch:1:results", world.Project, -1m)]));
        exception.Message.ShouldContain("/entries/0/quantity");
        exception.Message.ShouldContain("invalid_value");
    }

    [Fact]
    public async Task RecordUsageBatch_UnstorableText_422()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(
            world,
            Batch(
                "usage-unstorable",
                [
                    Entry("batch:\0:results", world.Project),                      // NUL in the billing key
                    Entry("batch:2:results", world.Project, unit: "batch\0"),      // NUL in the unit
                    Entry("batch:\uD800:results", world.Project),                  // lone high surrogate in the key
                    Entry("batch:4:results", world.Project, unit: "\uDC00"),       // lone low surrogate in the unit
                    Entry("batch:\U0001F3AF:results", world.Project),              // a real pair is storable
                ]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        refused.Error.Details!.Select(detail => (detail.Index, detail.Path, detail.Reason)).ShouldBe(
            [
                ((int?)0, "/entries/0/billing_key", "invalid_value"),
                (1, "/entries/1/unit", "invalid_value"),
                (2, "/entries/2/billing_key", "invalid_value"),
                (3, "/entries/3/unit", "invalid_value"),
            ],
            "U+0000 and a lone surrogate cannot be stored as text, so they are refused before the statement");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0);

        var accepted = await SendAsync(
            world,
            Batch("usage-astral", [Entry("batch:\U0001F3AF:results", world.Project)]));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value!.Inserted.ShouldBe(["batch:\U0001F3AF:results"], "a surrogate pair is one storable character");
    }

    [Fact]
    public async Task RecordUsageBatch_UnitLength_CountsCharactersAndCapsAt32()
    {
        await using var world = await StartAsync();

        var atLimit = string.Concat(Enumerable.Repeat("\U0001F3AF", IUsageLedger.MaxUnitLength));
        atLimit.Length.ShouldBe(IUsageLedger.MaxUnitLength * 2, "the fixture unit must be multibyte to prove the rule");

        var accepted = await RecordAsync(
            world,
            [Entry("batch:1:results", world.Project, unit: atLimit)],
            cancellationToken: TestContext.Current.CancellationToken);
        accepted.Inserted.ShouldBe(["batch:1:results"]);
        (await ColumnAsync(world, world.Org, "batch:1:results", "char_length(unit)::text")).ShouldBe("32");

        var overLimit = atLimit + "\U0001F3AF";
        var refused = await SendAsync(
            world,
            Batch("usage-unit-over", [Entry("batch:2:results", world.Project, unit: overLimit)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(422);
        var detail = refused.Error.Details!.ShouldHaveSingleItem();
        detail.Index.ShouldBe(0);
        detail.Path.ShouldBe("/entries/0/unit");
        detail.Reason.ShouldBe("too_long");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
    }
}

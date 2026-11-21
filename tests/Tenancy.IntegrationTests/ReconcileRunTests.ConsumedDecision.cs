using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    /// <summary>
    /// A decision a release consumed stays exactly the row it saw once late evidence produces the next evaluation. No
    /// release-decision pointer exists yet, so the consumer is a table made here, in this test's own database, with the
    /// composite foreign key every later pointer to an evaluation will carry; it is never created by a migration.
    /// </summary>
    [Fact]
    public async Task ReleaseConsumedDecision_ByteIdentical_AfterN1()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: owned.Name);
        await StartEvaluatedIncompleteRunAsync(world);

        var consumedId = await world.ScalarAsync<Guid>(LiveEvaluationBy(world, 1, "id"));
        var consumedBytes = await LiveEvaluationSnapshotAsync(world, revision: 1);
        await world.Fixture.ExecuteAsync(
            world.Database,
            "create table public.consumed_decision_probe (org_id uuid not null, evaluation_id uuid not null, "
            + "decision text not null, primary key (org_id, evaluation_id), "
            + "foreign key (org_id, evaluation_id) references public.evaluation (org_id, id)); "
            + $"insert into public.consumed_decision_probe values ('{world.Org:D}', '{consumedId:D}', "
            + $"(select decision from public.evaluation where id = '{consumedId:D}'))");
        var reference = await ConsumedReferenceAsync(world, consumedId);

        await AppendLateEvaluationAsync(world, revision: 2);

        (await EvaluationRowsAsync(world)).ShouldBe(2, "late evidence decided through a new row");
        (await world.ScalarAsync<Guid>(LiveEvaluationBy(world, 2, "id")))
            .ShouldNotBe(consumedId, "the next evaluation is a row of its own");
        (await LiveEvaluationSnapshotAsync(world, revision: 1))
            .ShouldBe(consumedBytes, "every persisted column of the consumed row is unchanged");
        (await ConsumedReferenceAsync(world, consumedId)).ShouldBe(reference, "the consuming reference is unchanged");
        (await world.ScalarAsync<string>(
            $"select decision from public.evaluation where id = '{consumedId:D}'"))
            .ShouldBe(reference.Split('|')[1], "the decision the release saw is the decision stored");
    }

    private static string LiveEvaluationBy(IngestTestWorld world, int revision, string column) =>
        $"select {column} from public.evaluation where mode = 'live' and org_id = '{world.Org:D}' "
        + $"and run_id = '{world.RunId:D}' and evidence_revision = {revision}";

    private static Task<string> ConsumedReferenceAsync(IngestTestWorld world, Guid evaluationId) =>
        world.ScalarAsync<string>(
            "select org_id::text || '|' || decision || '|' || evaluation_id::text "
            + $"from public.consumed_decision_probe where evaluation_id = '{evaluationId:D}'");
}

using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The minter is the one place every artifact insert goes through, so its run-project refusal is pinned without a
/// database: a refusal returns before the context factory is touched, and a pass-through reaches the factory, whose
/// fake throws. Removing the project comparison therefore turns the refusal tests red.
/// </summary>
public sealed class EvidenceArtifactMinterRunProjectTests
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Checksum = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Project = Guid.CreateVersion7();

    private sealed class ReachedInsertException : Exception;

    private sealed class ThrowingFactory : ITenantDbContextFactory<EvidenceDbContext>
    {
        public EvidenceDbContext Create() => throw new ReachedInsertException();
    }

    private sealed class FixedRuns(RunPlanLookup? answer) : IRunPlanReader
    {
        public int Calls { get; private set; }

        public ValueTask<RunPlanLookup?> FindAsync(Guid runId, string executionKey, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(answer);
        }
    }

    private static ValueTask<Result<ArtifactRecord>> MintAsync(FixedRuns runs, RunPlanLookup? knownRun = null) =>
        new ArtifactMinter(new ThrowingFactory(), runs)
            .MintAsync(Org, Project, Guid.CreateVersion7(), ExecutionKey, 1, "trace", "application/zip", 1024, Checksum, CancellationToken.None, "normal", knownRun);

    [Fact]
    public async Task Mint_RunOfAnotherProject_IsRefusedBeforeAnyInsert()
    {
        var runs = new FixedRuns(new RunPlanLookup(Guid.CreateVersion7(), "active", true));

        var result = await MintAsync(runs);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(EvidenceErrors.ArtifactRunProjectMismatchCode);
    }

    [Fact]
    public async Task Mint_KnownRunOfAnotherProject_IsRefusedWithoutReadingTheRunAgain()
    {
        var runs = new FixedRuns(new RunPlanLookup(Project, "active", true));

        var result = await MintAsync(runs, new RunPlanLookup(Guid.CreateVersion7(), "active", true));

        result.Error!.Code.ShouldBe(EvidenceErrors.ArtifactRunProjectMismatchCode);
        runs.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Mint_InvisibleRun_IsRefusedAsNotFound()
    {
        var result = await MintAsync(new FixedRuns(null));

        result.Error!.Code.ShouldBe(EvidenceErrors.RunNotFound.Code);
    }

    [Fact]
    public async Task Mint_RunOfTheSameProject_ReachesTheInsert() =>
        await Should.ThrowAsync<ReachedInsertException>(async () =>
            await MintAsync(new FixedRuns(new RunPlanLookup(Project, "active", true))));

    [Fact]
    public async Task Mint_RunOfADeletingProject_IsRefusedBeforeAnyInsert()
    {
        var result = await MintAsync(new FixedRuns(new RunPlanLookup(Project, "deleted", true)));

        result.Error!.Code.ShouldBe(EvidenceErrors.ProjectDeleting.Code);
    }

    [Fact]
    public async Task Mint_KnownRunOfADeletingProject_IsRefusedWithoutReadingTheRunAgain()
    {
        var runs = new FixedRuns(null);

        var result = await MintAsync(runs, new RunPlanLookup(Project, "deleted", true));

        result.Error!.Code.ShouldBe(EvidenceErrors.ProjectDeleting.Code);
        runs.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Mint_UnplannedExecution_IsRefusedBeforeAnyInsert()
    {
        var result = await MintAsync(new FixedRuns(new RunPlanLookup(Project, "active", false)));

        result.Error!.Code.ShouldBe(EvidenceErrors.UnknownExecutionKey.Code);
    }

    [Fact]
    public async Task Mint_RefusesWhatTheUploadUrlPathRefuses_AndPassesTheSameControl()
    {
        ArtifactMinter.DeletingRefusal(new RunPlanLookup(Project, "deleted", true))!.Code.ShouldBe(EvidenceErrors.ProjectDeleting.Code);
        ArtifactMinter.PlanRefusal(new RunPlanLookup(Project, "active", false))!.Code.ShouldBe(EvidenceErrors.UnknownExecutionKey.Code);
        ArtifactMinter.DeletingRefusal(new RunPlanLookup(Project, "suspended", true)).ShouldBeNull();
        ArtifactMinter.PlanRefusal(new RunPlanLookup(Project, "active", true)).ShouldBeNull();
        await Should.ThrowAsync<ReachedInsertException>(async () =>
            await MintAsync(new FixedRuns(new RunPlanLookup(Project, "suspended", true))));
    }

    [Fact]
    public async Task Mint_KnownUnplannedRun_IsRefusedWithoutReadingTheRunAgain()
    {
        var runs = new FixedRuns(null);

        var result = await MintAsync(runs, new RunPlanLookup(Project, "active", false));

        result.Error!.Code.ShouldBe(EvidenceErrors.UnknownExecutionKey.Code);
        runs.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Mint_DeletingProjectWithUnplannedKey_AnswersProjectDeleting()
    {
        var result = await MintAsync(new FixedRuns(new RunPlanLookup(Project, "deleted", false)));

        result.Error!.Code.ShouldBe(EvidenceErrors.ProjectDeleting.Code);
    }
}

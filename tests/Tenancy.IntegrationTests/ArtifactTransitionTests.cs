using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The artifact state machine as data: every state, every allowed edge and every distinct-state pair the guard must
/// refuse. The domain suite and the PostgreSQL suite both read it, so a state or an edge cannot drift between them.
/// </summary>
internal static class ArtifactStateMachine
{
    /// <summary>The six states of the contract, in the aggregate's order.</summary>
    public static IReadOnlyList<string> States => ArtifactState.All;

    /// <summary>The allow-list the aggregate and the trigger share.</summary>
    public static TheoryData<string, string> AllowedEdges() => new()
    {
        { ArtifactState.PendingUpload, ArtifactState.Uploaded },
        { ArtifactState.PendingUpload, ArtifactState.Ready },
        { ArtifactState.PendingUpload, ArtifactState.Rejected },
        { ArtifactState.Uploaded, ArtifactState.Ready },
        { ArtifactState.Uploaded, ArtifactState.Rejected },
        { ArtifactState.Ready, ArtifactState.Rejected },
        { ArtifactState.Ready, ArtifactState.PendingDelete },
        { ArtifactState.PendingDelete, ArtifactState.Deleted },
    };

    /// <summary>Every ordered pair of distinct states outside <see cref="AllowedEdges"/>.</summary>
    public static TheoryData<string, string> IllegalEdges()
    {
        var allowed = new HashSet<(string From, string To)>
        {
            (ArtifactState.PendingUpload, ArtifactState.Uploaded),
            (ArtifactState.PendingUpload, ArtifactState.Ready),
            (ArtifactState.PendingUpload, ArtifactState.Rejected),
            (ArtifactState.Uploaded, ArtifactState.Ready),
            (ArtifactState.Uploaded, ArtifactState.Rejected),
            (ArtifactState.Ready, ArtifactState.Rejected),
            (ArtifactState.Ready, ArtifactState.PendingDelete),
            (ArtifactState.PendingDelete, ArtifactState.Deleted),
        };

        var data = new TheoryData<string, string>();
        foreach (var from in States)
        {
            foreach (var to in States)
            {
                if (from != to && !allowed.Contains((from, to)))
                {
                    data.Add(from, to);
                }
            }
        }

        return data;
    }
}

public sealed class ArtifactTransitionTests
{
    public static TheoryData<string> States => [.. ArtifactStateMachine.States];

    public static TheoryData<string, string> AllowedEdges => ArtifactStateMachine.AllowedEdges();

    public static TheoryData<string, string> IllegalEdges => ArtifactStateMachine.IllegalEdges();

    [Fact]
    public void StateMachine_Matrix_CoversEveryDistinctPair()
    {
        ArtifactStateMachine.States.Count.ShouldBe(6);
        ArtifactStateMachine.AllowedEdges().Count.ShouldBe(8);
        ArtifactStateMachine.IllegalEdges().Count.ShouldBe(22);
        (ArtifactStateMachine.AllowedEdges().Count + ArtifactStateMachine.IllegalEdges().Count).ShouldBe(30);
    }

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public void Artifact_TransitionTo_Illegal_ThrowsInvalidOperation(string from, string to)
    {
        var artifact = Create(from);

        Should.Throw<InvalidOperationException>(() => artifact.TransitionTo(to));

        artifact.State.ShouldBe(from);
        artifact.Version.ShouldBe(7);
    }

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void Transition_AllowedEdges_Succeed(string from, string to)
    {
        var artifact = Create(from);

        var next = artifact.TransitionTo(to);

        next.State.ShouldBe(to);
        AssertOtherColumnsUnchanged(artifact, next);
        artifact.State.ShouldBe(from);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Transition_SameState_NoOp_Succeeds(string state)
    {
        var artifact = Create(state);

        var next = artifact.TransitionTo(state);

        next.State.ShouldBe(state);
        AssertOtherColumnsUnchanged(artifact, next);
    }

    [Theory]
    [InlineData(ArtifactState.Rejected)]
    [InlineData(ArtifactState.PendingDelete)]
    [InlineData(ArtifactState.Deleted)]
    public void MarkReady_IllegalSource_ThrowsInvalidOperation(string from) =>
        Should.Throw<InvalidOperationException>(() => Create(from).MarkReady(1024));

    [Theory]
    [InlineData(ArtifactState.PendingDelete)]
    [InlineData(ArtifactState.Deleted)]
    public void MarkRejected_IllegalSource_ThrowsInvalidOperation(string from) =>
        Should.Throw<InvalidOperationException>(() => Create(from).MarkRejected(ArtifactFailureCode.ParseCrashed));

    [Fact]
    public void MarkRejected_ReadyParseVerdict_Succeeds()
    {
        var artifact = Create(ArtifactState.Ready);

        var next = artifact.MarkRejected(ArtifactFailureCode.ParseCrashed);

        next.State.ShouldBe(ArtifactState.Rejected);
        next.FailureCode.ShouldBe(ArtifactFailureCode.ParseCrashed);
        next.Bytes.ShouldBe(artifact.Bytes);
        next.Checksum.ShouldBe(artifact.Checksum);
        next.ObjectKey.ShouldBe(artifact.ObjectKey);
        next.ScanState.ShouldBe(artifact.ScanState);
        artifact.State.ShouldBe(ArtifactState.Ready);
    }

    [Theory]
    [InlineData("")]
    [InlineData("READY")]
    [InlineData("unknown")]
    public void Transition_UnknownTarget_ThrowsArgumentOutOfRange(string to) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Create(ArtifactState.PendingUpload).TransitionTo(to));

    // The handler's 23514 branch is unreachable through a committed competing write: the version trigger bumps the
    // token on every committed state change, so the completion's optimistic update matches no row first. These pin the
    // mapping that branch uses, which is what turns the trigger's refusal into the contract's 409.
    [Fact]
    public void IsIllegalTransition_TriggerCheckViolation_IsMapped() =>
        ArtifactTransitionFault.IsIllegalTransition(new DbUpdateException(
            "save",
            new PostgresException(
                "artifact_illegal_transition: ready -> pending_upload",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.CheckViolation))).ShouldBeTrue();

    [Fact]
    public void IsIllegalTransition_OtherCheckViolation_IsNotMapped() =>
        ArtifactTransitionFault.IsIllegalTransition(new DbUpdateException(
            "save",
            new PostgresException(
                "artifact_state_check",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.CheckViolation))).ShouldBeFalse();

    [Fact]
    public void IsIllegalTransition_WithoutPostgresCause_IsNotMapped() =>
        ArtifactTransitionFault.IsIllegalTransition(
            new DbUpdateException("save", new InvalidOperationException("not the trigger"))).ShouldBeFalse();

    private static Artifact Create(string state) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        new string('a', 64), 1, ArtifactKind.Trace, "artifact-key", "application/zip",
        "sha256:" + new string('b', 64), ArtifactSensitivity.Normal,
        bytes: 1024, scanState: ArtifactScanState.Clean, state: state, version: 7);

    private static void AssertOtherColumnsUnchanged(Artifact before, Artifact after)
    {
        after.Id.ShouldBe(before.Id);
        after.OrgId.ShouldBe(before.OrgId);
        after.ProjectId.ShouldBe(before.ProjectId);
        after.RunId.ShouldBe(before.RunId);
        after.ExecutionKey.ShouldBe(before.ExecutionKey);
        after.AttemptNo.ShouldBe(before.AttemptNo);
        after.AttemptId.ShouldBe(before.AttemptId);
        after.Kind.ShouldBe(before.Kind);
        after.ObjectKey.ShouldBe(before.ObjectKey);
        after.MediaType.ShouldBe(before.MediaType);
        after.Bytes.ShouldBe(before.Bytes);
        after.Checksum.ShouldBe(before.Checksum);
        after.Sensitivity.ShouldBe(before.Sensitivity);
        after.ScanState.ShouldBe(before.ScanState);
        after.ExpiresAt.ShouldBe(before.ExpiresAt);
        after.FailureCode.ShouldBe(before.FailureCode);
        after.Version.ShouldBe(before.Version);
        after.CreatedAt.ShouldBe(before.CreatedAt);
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
    }
}

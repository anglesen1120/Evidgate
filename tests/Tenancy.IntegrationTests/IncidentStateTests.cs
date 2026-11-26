using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Healing.Domain;
using Evidgate.Modules.Healing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class IncidentStateTests
{
    [Fact(DisplayName = "incident_state_machine")]
    public void IncidentStateMachine_ValidTransitions_PreserveFirstFailure()
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var run = Guid.CreateVersion7();
        var attempt = Guid.CreateVersion7();
        var testCase = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();
        var incident = Incident.Open(org, project, run, attempt, testCase, revision, "checkout", "playwright");
        incident.Id.Version.ShouldBe(7);
        incident.State.ShouldBe(IncidentState.Open);
        incident.ContextState.ShouldBe(IncidentContextState.Pending);

        incident.Triage("product defect").IsSuccess.ShouldBeTrue();
        incident.State.ShouldBe(IncidentState.Triaged);
        incident.ContextState.ShouldBe(IncidentContextState.Pending);
        incident.Close().IsSuccess.ShouldBeTrue();
        incident.State.ShouldBe(IncidentState.Closed);
        incident.ContextState.ShouldBe(IncidentContextState.Pending);
        incident.SettleContext(IncidentContextState.Complete).IsSuccess.ShouldBeTrue();
        incident.ContextState.ShouldBe(IncidentContextState.Complete);
        incident.State.ShouldBe(IncidentState.Closed);

        incident.OrgId.ShouldBe(org);
        incident.ProjectId.ShouldBe(project);
        incident.RunId.ShouldBe(run);
        incident.AttemptId.ShouldBe(attempt);
        incident.CaseId.ShouldBe(testCase);
        incident.CaseRevisionId.ShouldBe(revision);
        incident.ExecutionKey.ShouldBe("checkout");
        incident.Engine.ShouldBe("playwright");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Triage_BlankLabel_RefusesWithoutChangingState(string? label)
    {
        var incident = Open();
        incident.Triage(label).Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.RequestInvalid);
        incident.State.ShouldBe(IncidentState.Open);
        incident.ContextState.ShouldBe(IncidentContextState.Pending);
        incident.Triage("product defect").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IncidentStateMachine_SkippedOrRepeatedTransition_RefusesWithoutChangingState()
    {
        var incident = Open();
        incident.SettleContext(IncidentContextState.Partial).IsSuccess.ShouldBeTrue();
        incident.Close().Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.InvalidTransition);
        incident.State.ShouldBe(IncidentState.Open);
        incident.Triage("product defect").IsSuccess.ShouldBeTrue();
        incident.Triage("product defect").Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.InvalidTransition);
        incident.State.ShouldBe(IncidentState.Triaged);
        incident.Close().IsSuccess.ShouldBeTrue();
        incident.Close().Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.InvalidTransition);
        incident.State.ShouldBe(IncidentState.Closed);
        incident.Triage("product defect").Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.InvalidTransition);
        incident.State.ShouldBe(IncidentState.Closed);
        incident.ContextState.ShouldBe(IncidentContextState.Partial);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettleContext_TerminalState_RefusesAllFurtherSettlements(bool complete)
    {
        var incident = Open();
        var target = complete ? IncidentContextState.Complete : IncidentContextState.Partial;
        incident.SettleContext(target).IsSuccess.ShouldBeTrue();
        incident.ContextState.ShouldBe(target);
        foreach (var next in Enum.GetValues<IncidentContextState>())
        {
            incident.SettleContext(next).Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.InvalidTransition);
            incident.ContextState.ShouldBe(target);
            incident.State.ShouldBe(IncidentState.Open);
        }
    }

    [Theory]
    [InlineData((int)IncidentContextState.Pending)]
    [InlineData(-1)]
    public void SettleContext_InvalidDestination_LeavesPending(int target)
    {
        var incident = Open();
        incident.SettleContext((IncidentContextState)target).Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.InvalidTransition);
        incident.ContextState.ShouldBe(IncidentContextState.Pending);
        incident.State.ShouldBe(IncidentState.Open);
        incident.SettleContext(IncidentContextState.Complete).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void WriteModel_IncidentUpdate_UsesServerVersionAndTimestamp()
    {
        var options = new DbContextOptionsBuilder<HealingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=evidgate;Username=evidgate_app")
            .UseTenantModel(typeof(HealingDbContext).Assembly)
            .Options;
        using var context = new HealingDbContext(options);
        var entity = context.Model.FindEntityType(typeof(Incident)).ShouldNotBeNull();
        var version = entity.FindProperty(nameof(Incident.Version)).ShouldNotBeNull();
        version.IsConcurrencyToken.ShouldBeTrue();
        version.ValueGenerated.ShouldBe(ValueGenerated.OnUpdate);
        entity.FindProperty(nameof(Incident.UpdatedAt)).ShouldNotBeNull().ValueGenerated.ShouldBe(ValueGenerated.OnAddOrUpdate);
        var state = entity.FindProperty(nameof(Incident.State)).ShouldNotBeNull().GetValueConverter().ShouldNotBeNull();
        state.ConvertToProvider(IncidentState.Triaged).ShouldBe("triaged");
        state.ConvertFromProvider("closed").ShouldBe(IncidentState.Closed);
        var contextState = entity.FindProperty(nameof(Incident.ContextState)).ShouldNotBeNull().GetValueConverter().ShouldNotBeNull();
        contextState.ConvertToProvider(IncidentContextState.Partial).ShouldBe("partial");
        contextState.ConvertFromProvider("complete").ShouldBe(IncidentContextState.Complete);

        foreach (var linkage in new[] { "OrgId", "ProjectId", "RunId", "AttemptId", "CaseId", "CaseRevisionId", "ExecutionKey", "Engine" })
        {
            entity.FindProperty(linkage).ShouldNotBeNull().GetAfterSaveBehavior().ShouldBe(PropertySaveBehavior.Throw, linkage);
        }

        foreach (var movable in new[] { nameof(Incident.State), nameof(Incident.ContextState) })
        {
            entity.FindProperty(movable).ShouldNotBeNull().GetAfterSaveBehavior().ShouldBe(PropertySaveBehavior.Save, movable);
        }
    }

    private static Incident Open() => Incident.Open(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), "checkout", "playwright");
}

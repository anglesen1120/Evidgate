using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Authorization lives in the Mediator pipeline, so every handler class must declare
/// its permission via [Permission]; and a request handler must answer Result/Result&lt;T&gt;,
/// because that contract is what makes the pipeline's tenant and authorization stages
/// run for it.
/// </summary>
public class PipelineTests
{
    [Fact]
    public void Every_Handler_Declares_Permission()
    {
        var fixtures = new[]
        {
            typeof(Evidgate.Modules.Sample.Domain.CreateSampleCommand),
            typeof(Evidgate.Modules.Sample.Domain.CreateSampleHandler),
            typeof(Evidgate.Modules.Sample.Domain.HandlerMissingPermission),
            typeof(Evidgate.Modules.Sample.Domain.HandlerWithInvalidPermission),
        };

        var violations = ArchitectureRules.EveryHandlerDeclaresPermission(fixtures);

        // The command is not a handler and is not reported; the attributed
        // handler is clean; the missing and the malformed declarations are the
        // only violations — the malformed one is reported, not thrown.
        violations.Count.ShouldBe(2);
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerMissingPermission)));
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerWithInvalidPermission))
                && v.Contains("invalid permission 'RUN CREATE'"));
        violations.ShouldAllBe(v =>
            !v.Contains(nameof(Evidgate.Modules.Sample.Domain.CreateSampleHandler)));
    }

    /// <summary>
    /// 02.1.1: <c>[AuthenticatedMember]</c> replaces <c>[Permission]</c> only on the
    /// allowlisted tenant-creating handler; anywhere else, or next to a permission, it
    /// is a violation.
    /// </summary>
    [Fact]
    public void Authenticated_Member_Gate_Is_Closed_By_The_Allowlist()
    {
        var violations = ArchitectureRules.EveryHandlerDeclaresPermission(
        [
            typeof(Evidgate.Modules.Sample.Domain.HandlerWithUnlistedAuthenticatedMember),
            typeof(Evidgate.Modules.Sample.Domain.HandlerWithBothGates),
        ]);

        violations.Count.ShouldBe(3);
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerWithUnlistedAuthenticatedMember))
                && v.Contains("AuthenticatedMemberAllowlist"));
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerWithBothGates))
                && v.Contains("exactly one gate"));
        ArchitectureRules.AuthenticatedMemberAllowlist.Count.ShouldBe(1);
    }

    /// <summary>02.1.1 security review: only allowlisted code publishes the request tenant.</summary>
    [Fact]
    public void Only_Allowlisted_Types_Publish_The_Request_Tenant()
    {
        ArchitectureRules
            .OnlyAllowlistedTypesPublishTheTenant(SolutionLayout.EvidgateTypes())
            .ShouldBeEmpty();

        // Non-vacuity: the rule sees a setter parameter on a non-allowlisted type.
        ArchitectureRules
            .OnlyAllowlistedTypesPublishTheTenant([typeof(Evidgate.Modules.Sample.Domain.TenantPublishingFixture)])
            .ShouldHaveSingleItem();
    }

    /// <summary>02.7.1-T2.1: a mutation without [AuditedAction] fails the rule; the real solution is clean.</summary>
    [Fact]
    public void Every_Mutation_Declares_An_Audited_Action()
    {
        ArchitectureRules.EveryMutationDeclaresAnAuditedAction(SolutionLayout.EvidgateTypes()).ShouldBeEmpty();

        // Non-vacuity: a Result-returning command handler with no [AuditedAction] is reported,
        // one with it is not, and a query/DTO handler is out of scope.
        var violations = ArchitectureRules.EveryMutationDeclaresAnAuditedAction(
        [
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningResult),
            typeof(Evidgate.Modules.Sample.Domain.AuditedHandlerReturningResult),
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningDto),
        ]);

        violations.ShouldHaveSingleItem().ShouldContain(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningResult));
    }

    [Fact]
    public void Real_Solution_Handlers_All_Declare_Permission()
    {
        ArchitectureRules
            .EveryHandlerDeclaresPermission(SolutionLayout.EvidgateTypes())
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The pipeline's tenant and authorization stages only run for Result-carrying
    /// messages, so any other Mediator request handler is a violation — proven on
    /// synthetic violations first (the rule must fire, including for a Unit response).
    /// </summary>
    [Fact]
    public void Every_Pipeline_Handler_Returns_A_Result()
    {
        var fixtures = new[]
        {
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningDto),
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningUnit),
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningResult),
        };

        var violations = PipelineHandlerRules.EveryPipelineHandlerReturnsResult(fixtures);

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningDto)));
        violations.ShouldContain(v => v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningUnit)));
        violations.ShouldAllBe(v => !v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningResult)));
    }

    /// <summary>
    /// The rule that keeps a gated handler from answering a bare DTO: the stage that
    /// enforces [Permission], [AuthenticatedMember] or [CredentialAuthenticated] can only
    /// refuse through a Result, and a handler with no gate is outside this rule's scope.
    /// </summary>
    [Fact]
    public void Every_Gated_Handler_Returns_A_Result()
    {
        var violations = PipelineHandlerRules.EveryGatedHandlerReturnsResult(
        [
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningDto),
            typeof(Evidgate.Modules.Sample.Domain.HandlerWithAuthenticatedMemberReturningDto),
            typeof(Evidgate.Modules.Sample.Domain.HandlerWithCredentialReturningDto),
            typeof(Evidgate.Modules.Sample.Domain.HandlerWithoutGateReturningDto),
            typeof(Evidgate.Modules.Sample.Domain.HandlerReturningResult),
        ]);

        violations.Count.ShouldBe(3);
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningDto)) && v.Contains("Result"));
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerWithAuthenticatedMemberReturningDto)));
        violations.ShouldContain(v =>
            v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerWithCredentialReturningDto)));

        // The ungated DTO handler is what the other rule reports; this one must not.
        violations.ShouldAllBe(v =>
            !v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerWithoutGateReturningDto)));
        violations.ShouldAllBe(v => !v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningResult)));
    }

    [Fact]
    public void Real_Solution_Pipeline_Handlers_Return_A_Result()
    {
        var types = SolutionLayout.EvidgateTypes();

        // The scan must reach the solution's handlers: an empty run would pass vacuously.
        var handlers = types.Where(PipelineHandlerRules.IsRequestHandler).ToList();
        handlers.Count.ShouldBeGreaterThan(10);

        PipelineHandlerRules.EveryPipelineHandlerReturnsResult(handlers).ShouldBeEmpty();
        PipelineHandlerRules.EveryGatedHandlerReturnsResult(handlers).ShouldBeEmpty();
    }
}

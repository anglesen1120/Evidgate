using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.7 S2 + C2: authorization lives in the Mediator pipeline, so every handler
/// class must declare its permission via [Permission]; and 00 §5.5 C8: a request
/// handler must answer Result/Result&lt;T&gt;, because that contract is what makes
/// the pipeline's tenant stages run for it (the pinned skeleton slice excepted).
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

    [Fact]
    public void Real_Solution_Handlers_All_Declare_Permission()
    {
        ArchitectureRules
            .EveryHandlerDeclaresPermission(SolutionLayout.EvidgateTypes())
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The sprint-1 skeleton slice (01.1.2-T3), the pinned exemption of the C8
    /// contract rule: 01.1.2 §8 records that these routes carry no tenant scope and
    /// no permission, so their DTO responses are outside the C2 tenant stages until
    /// 04/05 replace them with Result-carrying commands. Pinned by full type name
    /// because the handlers are internal to their module assemblies.
    /// </summary>
    private static readonly string[] SkeletonSliceHandlers =
    [
        "Evidgate.Modules.Ingestion.Application.Commands.IngestManifestHandler",
        "Evidgate.Modules.Ingestion.Application.Commands.AcceptEventBatchHandler",
        "Evidgate.Modules.Orchestrator.Application.Commands.CreateRunHandler",
        "Evidgate.Modules.Orchestrator.Application.Commands.PlanExecutionsHandler",
        "Evidgate.Modules.Orchestrator.Application.Queries.GetCompletenessHandler",
        "Evidgate.Modules.Policy.Application.Queries.GetDecisionHandler",
    ];

    /// <summary>
    /// AC4 / 00 §5.5 C8: the C2 tenant stages only run for Result-carrying
    /// messages, so every other Mediator request handler is a violation — proven on
    /// synthetic violations first (the rule must fire, including for a Unit
    /// response), then over the real solution, where only the pinned skeleton slice
    /// may answer a bare DTO.
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

        var violations = ArchitectureRules.EveryPipelineHandlerReturnsResult(fixtures, []);

        violations.Count.ShouldBe(2);
        violations.ShouldContain(v => v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningDto)));
        violations.ShouldContain(v => v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningUnit)));
        violations.ShouldAllBe(v => !v.Contains(nameof(Evidgate.Modules.Sample.Domain.HandlerReturningResult)));
    }

    [Fact]
    public void Real_Solution_Pipeline_Handlers_Return_A_Result()
    {
        var types = SolutionLayout.EvidgateTypes();

        ArchitectureRules
            .EveryPipelineHandlerReturnsResult(types, SkeletonSliceHandlers)
            .ShouldBeEmpty();

        // The exemption list must name real handlers of this solution: a renamed or
        // deleted slice handler must fail here instead of silently widening the hole.
        var declared = types.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        SkeletonSliceHandlers.ShouldAllBe(name => declared.Contains(name));
    }
}

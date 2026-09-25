using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.7 S2 + C2: authorization lives in the Mediator pipeline, so every
/// handler class must declare its permission via [Permission].
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
}

using System.Reflection;
using Architecture.Tests.Rules;
using Evidgate.Evaluation;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.5 C1 and §5.7 P1/P3/P5: the evaluator is a pure function. Adding an <see cref="HttpClient"/> field, a
/// database driver reference or a module implementation reference must fail the real-graph assertion below.
/// </summary>
public class EvaluatorPurityTests
{
    [Fact]
    public void Real_Evaluator_Assembly_References_No_Io_And_No_Module_Implementation()
    {
        var references = ArchitectureRules.ReferencedAssemblyNames(typeof(PolicyEvaluator).Assembly);

        ArchitectureRules.EvaluatorStaysPure(references).ShouldBeEmpty();
        references.ShouldContain("Evidgate.Modules.Evidence.Contracts");
    }

    [Fact]
    public void Purity_Rule_Rejects_An_Http_Db_Or_Module_Implementation_Reference()
    {
        var violations = ArchitectureRules.EvaluatorStaysPure([
            "System.Net.Http",
            "Npgsql",
            "Evidgate.Modules.Evidence",
            "Evidgate.Modules.Evidence.Contracts",
            "System.Runtime",
        ]);

        violations.Count.ShouldBe(3);
        violations.ShouldContain(violation => violation.Contains("System.Net.Http", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("Npgsql", StringComparison.Ordinal));
        violations.ShouldContain(violation => violation.Contains("Evidgate.Modules.Evidence", StringComparison.Ordinal));

        ArchitectureRules.EvaluatorStaysPure(["Evidgate.Modules.Evidence.Contracts", "System.Runtime"]).ShouldBeEmpty();
    }

    [Fact]
    public void Purity_Rule_Rejects_Any_Infrastructure_Assembly()
    {
        ArchitectureRules.EvaluatorStaysPure(["Evidgate.Modules.Evidence.Infrastructure"])
            .ShouldHaveSingleItem()
            .ShouldContain("infrastructure");
    }

    [Fact]
    public void Evaluator_Inputs_Expose_No_Mutable_Setters()
    {
        var mutable = typeof(PolicyEvaluator).Assembly.GetExportedTypes()
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.SetMethod is { } setter
                    && !setter.ReturnParameter.GetRequiredCustomModifiers()
                        .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
                .Select(property => $"{type.Name}.{property.Name}"))
            .ToArray();

        mutable.ShouldBeEmpty();
    }
}

using System.Reflection;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 00 §5.5 C6: an immutable aggregate of the Policy module carries no property setter — no <c>set</c> and no
/// <c>init</c> — so the write model cannot be mutated after construction and the database's REVOKE UPDATE/DELETE
/// is the second line of defence, not the only one. The rule takes its types explicitly, so the test can prove it
/// on a synthetic violation and then run the same function over the real module.
/// </summary>
public static class PolicyImmutabilityRules
{
    /// <summary>The namespace whose types are the Policy module's immutable aggregates.</summary>
    public const string DomainNamespace = "Evidgate.Modules.Policy.Domain";

    /// <summary>Every property of a <see cref="DomainNamespace"/> type that carries a set accessor.</summary>
    public static IReadOnlyList<string> NoPropertySetters(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var violations = new List<string>();

        foreach (var type in types
            .Where(type => type.Namespace == DomainNamespace)
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            foreach (var property in type
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                // SetMethod is the accessor itself, whatever its accessibility, and an `init` accessor is one too.
                if (property.SetMethod is not null)
                {
                    violations.Add(
                        $"{type.FullName}.{property.Name} has a set accessor: an immutable Policy aggregate must "
                        + "have none (00 §5.5 C6)");
                }
            }
        }

        return violations;
    }
}

/// <summary>
/// The Policy immutability rule, exercised twice: once against a synthetic type that must produce a violation, and
/// once against the real module.
/// </summary>
public class PolicyImmutabilityTests
{
    [Fact]
    public void A_Setter_On_A_Policy_Domain_Type_Is_A_Violation()
    {
        var violations = PolicyImmutabilityRules.NoPropertySetters(
            [typeof(Evidgate.Modules.Policy.Domain.MutablePolicyAggregate)]);

        violations.ShouldHaveSingleItem().ShouldContain("MutablePolicyAggregate");
    }

    [Fact]
    public void Real_Policy_Domain_Types_Have_No_Property_Setters()
    {
        var types = SolutionLayout.EvidgateTypes()
            .Where(type => type.Namespace == PolicyImmutabilityRules.DomainNamespace)
            .ToList();

        // The scan must actually see the module, or the rule below would pass vacuously.
        types.ShouldNotBeEmpty();
        PolicyImmutabilityRules.NoPropertySetters(types).ShouldBeEmpty();
    }
}

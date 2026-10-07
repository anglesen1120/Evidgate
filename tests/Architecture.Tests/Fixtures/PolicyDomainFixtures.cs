// Synthetic fixture for the Policy immutability rule (07.1.1-T1.2). The namespace mimics the real module
// (Evidgate.Modules.Policy.Domain) but the containing assembly is Architecture.Tests, so the real-solution scan
// (Evidgate.*.dll in the output directory) never sees it; the test passes it to the rule explicitly. Do not move
// this type into a production project.
namespace Evidgate.Modules.Policy.Domain;

/// <summary>Seeded violation fixture: a Policy Domain type carrying a setter, which the rule must report.</summary>
internal sealed class MutablePolicyAggregate
{
    public string Name { get; set; } = string.Empty;
}

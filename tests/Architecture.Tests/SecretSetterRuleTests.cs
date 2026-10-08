using System.Reflection;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// 10.1.1-T2 (SEC-PROV-02): a secret option property must not have a public setter
/// (<c>set</c> or <c>init</c>). The configuration binder only writes public setters,
/// so a non-public setter is what keeps a secret from being bound straight from
/// <c>IConfiguration</c> and forces it through <c>ISecretProvider</c>.
/// </summary>
/// <remarks>
/// A property is a secret option when its configuration key equals the
/// <c>IConfiguration</c> form of a <see cref="SecretNames.All"/> entry (compared
/// ignoring case, as <c>IConfiguration</c> does). The key is the type's
/// <c>SectionName</c> constant plus the property name, or — for an options type read
/// from an environment snapshot such as <c>MigratorOptions</c> — a
/// <c>&lt;Property&gt;Variable</c> constant holding the secret name.
/// </remarks>
public class SecretSetterRuleTests
{
    [Fact]
    public void Real_Secret_Option_Properties_Have_No_Public_Setter()
    {
        var result = SecretSetterRule.Scan(SolutionLayout.EvidgateTypes());

        result.Violations.ShouldBeEmpty(string.Join(Environment.NewLine, result.Violations));
    }

    [Fact]
    public void Every_Secret_Name_Maps_To_An_Option_Property()
    {
        // Guards the rule itself: a renamed section or property would otherwise make
        // the rule find nothing and pass vacuously.
        var result = SecretSetterRule.Scan(SolutionLayout.EvidgateTypes());

        SecretNames.All.Except(result.CoveredSecretNames, StringComparer.Ordinal)
            .ShouldBeEmpty("secret names no options property maps to");
    }

    [Fact]
    public void Synthetic_Public_Set_Is_Detected()
    {
        var result = SecretSetterRule.Scan([typeof(PublicSetSecretOptions)]);

        result.Violations.ShouldHaveSingleItem().ShouldContain(nameof(PublicSetSecretOptions.Pepper));
        result.CoveredSecretNames.ShouldHaveSingleItem().ShouldBe(SecretNames.CiPepper);
    }

    [Fact]
    public void Synthetic_Public_Init_Is_Detected_With_A_Differently_Cased_Section()
    {
        var result = SecretSetterRule.Scan([typeof(PublicInitSecretOptions)]);

        result.Violations.ShouldHaveSingleItem().ShouldContain(nameof(PublicInitSecretOptions.ConnectionString));
    }

    [Fact]
    public void Synthetic_Variable_Constant_Alias_Is_Detected()
    {
        var result = SecretSetterRule.Scan([typeof(PublicInitAliasOptions)]);

        result.Violations.ShouldHaveSingleItem().ShouldContain(nameof(PublicInitAliasOptions.ConnectionString));
        result.CoveredSecretNames.ShouldHaveSingleItem().ShouldBe(SecretNames.MigratorConnectionString);
    }

    [Fact]
    public void Synthetic_Non_Public_Setters_Pass()
    {
        var result = SecretSetterRule.Scan([typeof(NonPublicSetterSecretOptions)]);

        result.Violations.ShouldBeEmpty();
        result.CoveredSecretNames.ShouldBe(new[] { SecretNames.CiPepper, SecretNames.CiSigningKey }, ignoreOrder: true);
    }
}

/// <summary>
/// The pure rule behind <see cref="SecretSetterRuleTests"/>: given types, it reports
/// every secret option property with a public setter and the secret names it mapped.
/// </summary>
internal static class SecretSetterRule
{
    private const string SectionNameField = "SectionName";
    private const string VariableSuffix = "Variable";

    internal sealed record ScanResult(IReadOnlyList<string> Violations, IReadOnlySet<string> CoveredSecretNames);

    internal static ScanResult Scan(IEnumerable<Type> types)
    {
        var secretByKey = SecretNames.All.ToDictionary(
            SecretNames.ToConfigurationKey,
            name => name,
            StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            var section = ConstantString(type, SectionNameField);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var secret = SecretFor(type, property, section, secretByKey);
                if (secret is null)
                {
                    continue;
                }

                covered.Add(secret);

                if (property.SetMethod is { IsPublic: true })
                {
                    violations.Add(
                        $"{type.FullName}.{property.Name} (secret {secret}) has a public setter; make it internal or private so the binder cannot write it");
                }
            }
        }

        return new ScanResult(violations, covered);
    }

    private static string? SecretFor(
        Type type,
        PropertyInfo property,
        string? section,
        Dictionary<string, string> secretByKey)
    {
        if (section is not null && secretByKey.TryGetValue($"{section}:{property.Name}", out var bySection))
        {
            return bySection;
        }

        var alias = ConstantString(type, property.Name + VariableSuffix);

        return alias is not null && SecretNames.All.Contains(alias, StringComparer.Ordinal) ? alias : null;
    }

    private static string? ConstantString(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field
            ? field.GetRawConstantValue() as string
            : null;
}

internal sealed class PublicSetSecretOptions
{
    public const string SectionName = "EVIDGATE:Ci";

    public string? Pepper { get; set; }

    public int PepperVersion { get; set; }
}

internal sealed class PublicInitSecretOptions
{
    public const string SectionName = "evidgate:database";

    public string? ConnectionString { get; init; }
}

internal sealed class PublicInitAliasOptions
{
    public const string ConnectionStringVariable = SecretNames.MigratorConnectionString;

    public string? ConnectionString { get; init; }
}

internal sealed class NonPublicSetterSecretOptions
{
    public const string SectionName = "EVIDGATE:Ci";

    public string? SigningKey { get; internal set; }

    public string? Pepper { get; private init; }
}

using System.Reflection;
using Evidgate.Modules.Healing.Contracts;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The provider port carries text and attribution only: no tool, command, merge, write, URL-fetch or credential field
/// exists on the request, so a prompt-injected instruction has nothing to call. The absence is intentional and pinned
/// by an allowlist — adding a member fails here, in review of the change that adds it.
/// </summary>
public sealed class HealingLlmRequestShapeTests
{
    private static readonly string[] AllowedMembers =
    [
        "CandidateId:Guid?", "IncidentId:Guid?", "Model:String", "Payload:String", "ProjectId:Guid",
        "PromptVersion:String", "Purpose:String", "ResponseSchema:String",
    ];

    private static readonly string[] CapabilityWords =
    [
        "tool", "function", "command", "shell", "merge", "write", "file", "path", "url", "uri", "fetch", "credential",
        "secret", "token", "key", "password", "header", "policy",
    ];

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.Name != "EqualityContract");

    private static string Describe(PropertyInfo p) =>
        p.Name + ":" + (Nullable.GetUnderlyingType(p.PropertyType) is { } u ? u.Name + "?" : p.PropertyType.Name);

    [Fact]
    public void HealingLlmRequest_HasNoToolOrMergeFields()
    {
        Properties(typeof(HealingLlmRequest)).Select(Describe).Order(StringComparer.Ordinal).ShouldBe(AllowedMembers);
        typeof(HealingLlmRequest).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null).ShouldBeEmpty();
    }

    [Fact]
    public void HealingLlmRequest_NoMemberNameSuggestsACapability()
    {
        var names = Properties(typeof(HealingLlmRequest)).Select(p => p.Name)
            .Where(n => n != "PromptVersion").ToList();

        foreach (var word in CapabilityWords)
        {
            names.ShouldNotContain(n => n.Contains(word, StringComparison.OrdinalIgnoreCase), word);
        }
    }

    [Fact]
    public void HealingLlmRequest_OnlyScalarTextAndIdentityTypes()
    {
        Properties(typeof(HealingLlmRequest)).Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .ShouldAllBe(t => t == typeof(string) || t == typeof(Guid));
    }

    [Fact]
    public void IHealingLlmProvider_OffersExactlyOneTextInTextOutCall()
    {
        typeof(IHealingLlmProvider).GetMembers(BindingFlags.Instance | BindingFlags.Public).Select(m => m.Name)
            .ShouldBe(["InvokeAsync"]);
        typeof(IHealingLlmProvider).GetMethod("InvokeAsync")!.GetParameters().Select(p => p.ParameterType)
            .ShouldBe([typeof(HealingLlmRequest), typeof(CancellationToken)]);
    }
}

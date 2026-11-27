using Evidgate.Modules.Authoring.Application.Import;

namespace Tenancy.IntegrationTests;

/// <summary>Builders of requirement import rows shared by the requirement import test classes.</summary>
internal static class RequirementSample
{
    /// <summary>A requirement row.</summary>
    /// <param name="index">The 1-based data-row position.</param>
    /// <param name="key">The file-local key.</param>
    /// <param name="parentKey">The requirement row above it, or null.</param>
    /// <param name="kind">The requirement kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="body">The body, or null.</param>
    /// <param name="code">The external reference, or null.</param>
    /// <param name="featureNode">The feature node cell, or null.</param>
    internal static ImportRow Row(
        int index,
        string key,
        string? parentKey,
        string? kind,
        string? title,
        string? body = null,
        string? code = null,
        string? featureNode = null) => new(
            index,
            ImportRowKind.Requirement,
            key,
            parentKey,
            featureNode,
            kind,
            title,
            body,
            null,
            null,
            null,
            null,
            code,
            null);

    /// <summary>One story with two criteria, the outline every format test reuses.</summary>
    internal static List<ImportRow> Outline() =>
    [
        Row(1, "s1", null, "user_story", "Checkout", "As a shopper I pay for my cart."),
        Row(2, "a1", "s1", "acceptance_criterion", "Card is charged", "Card is charged"),
        Row(3, "a2", "s1", "acceptance_criterion", "Receipt is sent", "Receipt is sent"),
    ];
}

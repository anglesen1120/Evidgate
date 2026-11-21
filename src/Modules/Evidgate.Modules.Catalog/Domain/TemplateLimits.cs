namespace Evidgate.Modules.Catalog.Domain;

/// <summary>The one text size bound shared by the template scanner, the conformance rules and the renderer, so a file
/// one stage reads and a file another stage refuses cannot drift apart.</summary>
internal static class TemplateLimits
{
    /// <summary>Largest text of one template file or conformance rules document.</summary>
    internal const int MaxTextChars = 64 * 1024;
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architecture.Tests.Rules;

/// <summary>
/// The partial files of <c>EvidgateTelemetry</c> may not own or read a <c>Meter</c> themselves: a static
/// initialiser that reads another partial's field depends on the order the compiler receives the files in.
/// Only the holder file creates the meter, once.
/// </summary>
internal static class TelemetryMeterRules
{
    public const string Directory = "src/BuildingBlocks/Observability/";
    public const string HolderPath = Directory + "EvidgateMeter.cs";

    public static bool IsTelemetryPartial(string path) =>
        path.StartsWith(Directory + "EvidgateTelemetry", StringComparison.Ordinal)
        && path.EndsWith(".cs", StringComparison.Ordinal);

    public static List<string> Find(IEnumerable<(string Path, string Text)> files)
    {
        var violations = new List<string>();
        var holderCreations = 0;

        foreach (var (path, text) in files)
        {
            var isHolder = path == HolderPath;
            if (!isHolder && !IsTelemetryPartial(path))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var conditional = ConditionalSourceText.FirstLine(root);
            if (conditional != 0)
            {
                violations.Add($"{path}:{conditional} conditional text hides code from this scan");
            }

            var creations = root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                .Where(IsMeterCreation)
                .ToList();
            if (isHolder)
            {
                holderCreations += creations.Count;
                continue;
            }

            violations.AddRange(creations.Select(c => $"{path}:{Line(c)} creates a Meter outside the holder"));
            violations.AddRange(root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(f => IsMeterName(f.Declaration.Type))
                .Select(f => $"{path}:{Line(f)} declares a Meter field"));
            violations.AddRange(root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Expression is IdentifierNameSyntax { Identifier.ValueText: "Meter" })
                .Select(m => $"{path}:{Line(m)} reads an unqualified Meter"));
        }

        if (holderCreations != 1)
        {
            violations.Add($"{HolderPath} must create exactly one Meter, found {holderCreations}");
        }

        return violations;
    }

    private static bool IsMeterCreation(BaseObjectCreationExpressionSyntax node) =>
        node is ObjectCreationExpressionSyntax { Type: var type } && IsMeterName(type);

    private static bool IsMeterName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText == "Meter",
        QualifiedNameSyntax q => q.Right.Identifier.ValueText == "Meter",
        _ => false,
    };

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}

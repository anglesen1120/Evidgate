using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Architecture.Tests;

/// <summary>
/// The shared fail-closed rule of the syntax-walk boundary scanners: a file that carries disabled text or a
/// conditional directive is refused outright. A scanner parses with no symbols defined, so text under <c>#if SYMBOL</c>
/// is invisible to it yet compiled in production, and an <c>#else</c> branch holding the guard is visible but absent
/// when the symbol is set. Looking only for disabled text that names the scanner's own needle misses both.
/// </summary>
internal static class ConditionalSourceText
{
    /// <summary>The line of the first disabled text or conditional directive in the file, or 0 when there is none.</summary>
    public static int FirstLine(SyntaxNode root)
    {
        var directive = 0;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                return Line(trivia);
            }

            if (directive == 0 && (trivia.IsKind(SyntaxKind.IfDirectiveTrivia) || trivia.IsKind(SyntaxKind.ElifDirectiveTrivia)))
            {
                directive = Line(trivia);
            }
        }

        return directive;
    }

    private static int Line(SyntaxTrivia trivia) => trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}

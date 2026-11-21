using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architecture.Tests;

/// <summary>
/// The host's composer result is the one place filters may be attached, because it is the root of the whole route
/// tree: one bare call, exactly the pinned filters in dispatch order, and no receiver chain, stored copy, alias or
/// argument that could carry the tree to code the scan cannot follow.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private static List<string> ComposerResultFilterErrors(SyntaxNode root, string path)
    {
        var errors = new List<string>();
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => CallName(call) == "MapAuthoringEndpoints")
            .ToArray();
        if (calls.Length != 1)
        {
            errors.Add($"{path}:1: the host composer result must come from exactly one MapAuthoringEndpoints call, found {calls.Length}");
            return errors;
        }

        var call = calls[0];
        errors.AddRange(ComposerReceiverErrors(call, path));
        var applied = new List<string>();
        var end = ChainEnd(call, path, applied, errors);
        if (!applied.SequenceEqual(ComposerResultFilters, StringComparer.Ordinal))
        {
            errors.Add($"{path}:{Line(call)}: the composer result must add exactly {string.Join(", ", ComposerResultFilters)}, got {string.Join(", ", applied)}");
        }

        errors.AddRange(ComposerStatementErrors(end, path));
        return errors;
    }

    // A receiver chain before the composer may already have added a filter to the whole tree, so the composer result
    // must hang directly off the host's application object.
    private static List<string> ComposerReceiverErrors(InvocationExpressionSyntax call, string path)
    {
        var errors = new List<string>();
        if (call.Expression is not MemberAccessExpressionSyntax access)
        {
            errors.Add($"{path}:{Line(call)}: the host composer result must be called on the host application");
            return errors;
        }

        if (Unwrap(access.Expression) is not IdentifierNameSyntax
            || access.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
        {
            errors.Add($"{path}:{Line(call)}: the host composer result is reached through a receiver chain that can change every authoring route");
        }

        return errors;
    }

    // Walks the fluent chain above the composer call, recording each AddEndpointFilter<T> in dispatch order and
    // refusing every other call on the result.
    private static SyntaxNode ChainEnd(InvocationExpressionSyntax call, string path, List<string> applied, List<string> errors)
    {
        SyntaxNode node = call;
        while (node.Parent is MemberAccessExpressionSyntax access
            && access.Expression == node
            && access.Parent is InvocationExpressionSyntax outer
            && outer.Expression == access)
        {
            var name = access.Name.Identifier.ValueText;
            if (name == "AddEndpointFilter" && FilterType(outer) is { } filter)
            {
                applied.Add(filter);
            }
            else
            {
                errors.Add($"{path}:{Line(outer)}: {name} on the composer result can change every authoring route");
            }

            node = outer;
        }

        return node;
    }

    private static string? FilterType(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [IdentifierNameSyntax type] } }
            ? type.Identifier.ValueText
            : null;

    // Storing the result, copying it to an alias or passing it as an argument hands the tree to code the scan cannot
    // follow, so only a bare expression statement keeps the filter pair in one visible place.
    private static List<string> ComposerStatementErrors(SyntaxNode end, string path)
    {
        var errors = new List<string>();
        var node = end;
        while (node.Parent is ParenthesizedExpressionSyntax)
        {
            node = node.Parent;
        }

        if (node.Parent is not ExpressionStatementSyntax)
        {
            errors.Add($"{path}:{Line(end)}: the host composer result must be a bare expression statement, not stored, aliased or passed on");
        }

        return errors;
    }
}

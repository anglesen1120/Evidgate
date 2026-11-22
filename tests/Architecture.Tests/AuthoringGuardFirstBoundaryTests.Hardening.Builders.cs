using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architecture.Tests;

/// <summary>
/// Route-builder identity: which expressions hold a Map result (a parameter, a declarator, an assignment or a
/// branch of one), and what a fluent call is really called on once parentheses, null suppression, casts,
/// conditional access and coalescing are looked through.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    // Parentheses, a null-suppression `!` and a cast are transparent: they name the same builder.
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = suppressed.Operand;
                    continue;
                default:
                    return expression;
            }
        }
    }

    // `a?.B`, `a?.B()` and `a?.B.C()` all put the binding under a different parent, so the conditional access is
    // found by ancestry and its Expression is the receiver the binding is actually reached from.
    private static ConditionalAccessExpressionSyntax? ConditionalOf(MemberBindingExpressionSyntax binding) =>
        binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();

    // The identifier a fluent chain starts at, whatever wrappers, conditional access and nested calls lie between.
    private static ExpressionSyntax? ChainRoot(ExpressionSyntax expression)
    {
        var current = Unwrap(expression);
        return current switch
        {
            IdentifierNameSyntax identifier => identifier,
            MemberAccessExpressionSyntax access => ChainRoot(access.Expression),
            MemberBindingExpressionSyntax binding when ConditionalOf(binding) is { } conditional =>
                ChainRoot(conditional.Expression),
            ConditionalAccessExpressionSyntax conditional => ChainRoot(conditional.Expression),
            InvocationExpressionSyntax call => ChainRoot(call.Expression),
            ElementAccessExpressionSyntax element => ChainRoot(element.Expression),
            _ => null,
        };
    }

    private static string? ChainRootName(ExpressionSyntax expression) =>
        ChainRoot(expression) is IdentifierNameSyntax identifier ? identifier.Identifier.ValueText : null;

    // A fluent result still carries the Map result it was built from, and that shape is how the module builds its
    // groups (`routes.MapGroup("/v1").WithTags("authoring")`), so an allowed continuation is walked down to its
    // callee instead of being judged on the outer call alone.
    private static ExpressionSyntax Callee(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Expression,
        MemberBindingExpressionSyntax binding when ConditionalOf(binding) is { } conditional => conditional.Expression,
        _ => call.Expression,
    };

    // A value that holds a builder: a tracked name, either branch of a conditional or coalescing value, or a Map
    // result over a tracked receiver. One carrying branch is enough, because the registration may be handed it.
    private static bool ReceivesBuilder(ExpressionSyntax expression, HashSet<string> builders)
    {
        var value = Unwrap(expression);
        return value switch
        {
            IdentifierNameSyntax identifier => builders.Contains(identifier.Identifier.ValueText),
            ConditionalExpressionSyntax conditional =>
                ReceivesBuilder(conditional.WhenTrue, builders) || ReceivesBuilder(conditional.WhenFalse, builders),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression) =>
                ReceivesBuilder(binary.Left, builders) || ReceivesBuilder(binary.Right, builders),
            ConditionalAccessExpressionSyntax conditional =>
                ReceivesBuilder(conditional.Expression, builders)
                || (conditional.WhenNotNull is { } when && ReceivesBuilder(when, builders)),
            InvocationExpressionSyntax call => IsBuilderMapCall(call, builders)
                || (IsAllowedContinuation(call) && ReceivesBuilder(Callee(call), builders)),
            _ => false,
        };
    }

    // A bare builder after unwrapping: the shape a helper may register routes on directly.
    private static bool IsBareBuilder(ExpressionSyntax expression, HashSet<string> builders) =>
        Unwrap(expression) is IdentifierNameSyntax identifier && builders.Contains(identifier.Identifier.ValueText);

    // A receiver or value that is a builder or hangs off one: the identifier, a conditional access on it, a Map
    // result over it, or a member chain rooted at it (`a.Nested`). Used where a fluent route chain is not involved.
    private static bool IsTrackedReceiver(ExpressionSyntax receiver, HashSet<string> builders)
    {
        var value = Unwrap(receiver);
        return value switch
        {
            IdentifierNameSyntax identifier => builders.Contains(identifier.Identifier.ValueText),
            ConditionalAccessExpressionSyntax conditional => IsTrackedReceiver(conditional.Expression, builders),
            MemberBindingExpressionSyntax binding when ConditionalOf(binding) is { } conditional =>
                IsTrackedReceiver(conditional.Expression, builders),
            InvocationExpressionSyntax call => IsBuilderMapCall(call, builders),
            MemberAccessExpressionSyntax access => ChainRootName(access) is { } root && builders.Contains(root),
            _ => false,
        };
    }

    private static bool CallsOnBareBuilder(InvocationExpressionSyntax call, HashSet<string> builders) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => IsBareBuilder(access.Expression, builders),
        MemberBindingExpressionSyntax binding when ConditionalOf(binding) is { } conditional =>
            IsBareBuilder(conditional.Expression, builders),
        _ => false,
    };

    private static bool BuildersInArguments(InvocationExpressionSyntax call, HashSet<string> builders) =>
        call.ArgumentList.Arguments.Any(argument => ReceivesBuilder(argument.Expression, builders));

    private static bool CarriesBuilderMap(InvocationExpressionSyntax call, HashSet<string> builders) =>
        call.ArgumentList.Arguments.Any(argument => argument.Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(inner => IsBuilderMapCall(inner, builders)));

    private static bool IsBuilderMapCall(InvocationExpressionSyntax call, HashSet<string> builders) =>
        CallName(call).StartsWith("Map", StringComparison.Ordinal)
        && IsTrackedReceiver(call.Expression, builders);

    // The calls a route tree may carry: registration, the declared metadata chain and the permitted null guard.
    private static bool IsRouteTreeCall(InvocationExpressionSyntax call)
    {
        var name = CallName(call);
        return name.StartsWith("Map", StringComparison.Ordinal)
            || AllowedChainCalls.Contains(name, StringComparer.Ordinal)
            || IsAcceptsMetadata(name, call)
            || IsNullGuard(call);
    }

    private static bool IsNullGuard(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "ArgumentNullException" },
            Name.Identifier.ValueText: "ThrowIfNull",
        };

    // A Map result assigned to a variable leaves the fluent chain, so the chain checks never see a later call on it.
    // Declarators and simple assignments both count, and a value counts when it copies a builder, names one, or is a
    // Map result (or either branch of a conditional or coalescing value) over one.
    private static HashSet<string> RouteBuilderVariables(SyntaxNode root)
    {
        var names = ParameterBuilderNames(root);
        var declarations = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().ToArray();
        var assignments = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Left is IdentifierNameSyntax)
            .ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var declaration in declarations)
            {
                if (declaration.Initializer is { } initializer
                    && !names.Contains(declaration.Identifier.ValueText)
                    && ReceivesBuilder(initializer.Value, names))
                {
                    changed |= names.Add(declaration.Identifier.ValueText);
                }
            }

            foreach (var assignment in assignments)
            {
                var target = (IdentifierNameSyntax)assignment.Left;
                if (!names.Contains(target.Identifier.ValueText) && ReceivesBuilder(assignment.Right, names))
                {
                    changed |= names.Add(target.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    // Variables, not parameters: only a variable holding a Map result can carry a fluent call the chain checks
    // cannot reach, while a bare convention call on a parameter is caught by the non-Map helper check.
    private static HashSet<string> StoredBuilderVariables(SyntaxNode root)
    {
        var parameters = ParameterBuilderNames(root);
        return RouteBuilderVariables(root).Where(name => !parameters.Contains(name)).ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ParameterBuilderNames(SyntaxNode root) =>
        root.DescendantNodes().OfType<ParameterSyntax>()
            .Where(parameter => parameter.Type is { } type
                && RouteBuilderTypes.Contains(type.ToString().TrimEnd('?'), StringComparer.Ordinal))
            .Select(parameter => parameter.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
}

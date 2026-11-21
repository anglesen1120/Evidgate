using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Architecture.Tests;

/// <summary>
/// A read delegate may shape its query after the tenant guard, but anything that can answer the caller before the
/// mediator send hides the pipeline's authorisation. A same-type helper is refused when it throws or returns a
/// Results value, resolved through every partial part and with every overload failing closed; a local function or
/// lambda the handler invokes before the send counts the same way. Carried limit: a helper of another type, and a
/// helper of a helper, is not resolved.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private static readonly HashSet<SyntaxNode> NoSpine = [];

    private IEnumerable<string> HelperRefusalErrors(MethodDeclarationSyntax method, string? mediator, string path, string name)
    {
        if (method.Body is { } body)
        {
            var send = body.Statements.FirstOrDefault(statement => ContainsSend(statement, mediator));
            if (send is null)
            {
                return [];
            }

            var before = body.Statements.TakeWhile(statement => statement != send)
                .Select(statement => (Node: (SyntaxNode)statement, Spine: NoSpine));
            return before
                .Append((Node: (SyntaxNode)send, Spine: SendSpine(send, mediator)))
                .SelectMany(entry => Refusals(entry.Node, method, path, name, entry.Spine));
        }

        return method.ExpressionBody?.Expression is { } expression
            ? Refusals(expression, method, path, name, SendSpine(expression, mediator))
            : [];
    }

    private IEnumerable<string> Refusals(
        SyntaxNode node,
        MethodDeclarationSyntax owner,
        string path,
        string name,
        HashSet<SyntaxNode> sendSpine)
    {
        var helpers = SameTypeHelpers(owner);
        var locals = LocalRefusals(owner);
        var selfNames = SelfNames(owner);
        foreach (var call in OwnedNodes(node).OfType<InvocationExpressionSyntax>())
        {
            if (sendSpine.Contains(call))
            {
                continue;
            }

            if (IsStaticThrowHelper(call))
            {
                yield return $"{path}:{Line(call)}: {name} calls a helper refusal before mediator.Send, so the caller sees a failure that bypasses authorisation";
                continue;
            }

            if (HelperCallName(call, selfNames) is not { } helperName)
            {
                continue;
            }

            if (helpers.TryGetValue(helperName, out var overloads) && overloads.Any(HelperRefuses))
            {
                yield return $"{path}:{Line(call)}: {name} calls helper refusal {helperName} before mediator.Send, so the caller sees a failure that bypasses authorisation";
                continue;
            }

            if (call.Expression is IdentifierNameSyntax or GenericNameSyntax
                && locals.TryGetValue(helperName, out var refuses)
                && refuses)
            {
                yield return $"{path}:{Line(call)}: {name} calls local helper refusal {helperName} before mediator.Send, so the caller sees a failure that bypasses authorisation";
            }
        }
    }

    // The send spine is the mediator.Send call plus the wrapper invocations that reach it (ConfigureAwait/AsTask);
    // a helper call in an argument or another branch is not on it and still runs before authorisation.
    private static HashSet<SyntaxNode> SendSpine(SyntaxNode node, string? mediator)
    {
        var spine = new HashSet<SyntaxNode>();
        foreach (var send in OwnedNodes(node).OfType<InvocationExpressionSyntax>().Where(call => IsSend(call, mediator)))
        {
            spine.Add(send);
            for (var ancestor = send.Parent; ancestor is not null && ancestor != node; ancestor = ancestor.Parent)
            {
                if (ancestor is ExpressionSyntax)
                {
                    spine.Add(ancestor);
                }
            }
        }

        return spine;
    }

    // ArgumentNullException.ThrowIfNull stays the permitted null check. Every other ThrowIf* is a refusal, whether it
    // is written bare, on Guard or on an exception type, matched by the receiver's last identifier so a fully
    // qualified System.ArgumentOutOfRangeException and ObjectDisposedException are seen the same way.
    private static bool IsStaticThrowHelper(InvocationExpressionSyntax call)
    {
        var name = CallName(call);
        if (call.Expression is not MemberAccessExpressionSyntax access)
        {
            return name.StartsWith("ThrowIf", StringComparison.Ordinal);
        }

        var receiver = LastIdentifier(access.Expression);
        if (receiver == "ArgumentNullException" && name == "ThrowIfNull")
        {
            return false;
        }

        return receiver is "Guard" or "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException"
            or "ObjectDisposedException";
    }

    private static string LastIdentifier(ExpressionSyntax expression) =>
        expression.DescendantTokens()
            .LastOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken))
            .ValueText ?? string.Empty;

    // The handler's own type and every partial part of it in the scanned universe: a helper declared in another part
    // still resolves. An overloaded name keeps every overload, so one refusing overload refuses the call.
    private Dictionary<string, MethodDeclarationSyntax[]> SameTypeHelpers(MethodDeclarationSyntax owner)
    {
        if (owner.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not { } type)
        {
            return new Dictionary<string, MethodDeclarationSyntax[]>(StringComparer.Ordinal);
        }

        var typeName = type.Identifier.ValueText;
        var space = NamespaceOf(type);
        var declared = new List<MethodDeclarationSyntax>(
            type.SyntaxTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(part => part.Identifier.ValueText == typeName && NamespaceOf(part) == space)
                .SelectMany(part => part.Members.OfType<MethodDeclarationSyntax>()));
        foreach (var file in _universe)
        {
            if (!file.Text.Contains("partial", StringComparison.Ordinal)
                || !file.Text.Contains(typeName, StringComparison.Ordinal))
            {
                continue;
            }

            declared.AddRange(RootOf(file).DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(part => part.Identifier.ValueText == typeName && NamespaceOf(part) == space)
                .SelectMany(part => part.Members.OfType<MethodDeclarationSyntax>()));
        }

        return declared
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    }

    private SyntaxNode RootOf((string Path, string Text) file)
    {
        if (!_roots.TryGetValue(file.Path, out var root))
        {
            root = Parse(file.Text);
            _roots[file.Path] = root;
        }

        return root;
    }

    // `Check()`, `Check<int>()`, `Route.Check()` and `this.Check()` all name a helper of the handler's own type.
    // `this` is a ThisExpressionSyntax rather than an identifier, so it is matched structurally.
    private static string? HelperCallName(InvocationExpressionSyntax call, HashSet<string> selfNames) => call.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver, Name: IdentifierNameSyntax name }
            when selfNames.Contains(receiver.Identifier.ValueText) => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver, Name: GenericNameSyntax name }
            when selfNames.Contains(receiver.Identifier.ValueText) => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } =>
            name.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: GenericNameSyntax name } =>
            name.Identifier.ValueText,
        _ => null,
    };

    private static HashSet<string> SelfNames(MethodDeclarationSyntax owner)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (owner.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } type)
        {
            names.Add(type.Identifier.ValueText);
        }

        return names;
    }

    // A local function or lambda declared in the handler can answer the caller too, but only an invoked one matters:
    // an unused declaration stays green.
    private static Dictionary<string, bool> LocalRefusals(MethodDeclarationSyntax owner)
    {
        var refusals = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var local in owner.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            AddRefusal(
                refusals,
                local.Identifier.ValueText,
                local.ReturnType.ToString().Contains("IResult", StringComparison.Ordinal)
                || local.DescendantNodes().Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax));
        }

        foreach (var declarator in owner.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not AnonymousFunctionExpressionSyntax lambda)
            {
                continue;
            }

            AddRefusal(
                refusals,
                declarator.Identifier.ValueText,
                DeclaresResultsValue(declarator, lambda)
                || lambda.DescendantNodes().Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax));
        }

        return refusals;
    }

    // A delegate typed, or lambda return-typed, as a Results value: the declared type (`Func<IResult> f = …`) and the
    // text between `=` and `=>` (`var f = IResult () => …`) both name it, whichever way the lambda node is spanned.
    private static bool DeclaresResultsValue(VariableDeclaratorSyntax declarator, AnonymousFunctionExpressionSyntax lambda)
    {
        var declared = declarator.Parent is VariableDeclarationSyntax declaration ? declaration.Type.ToString() : string.Empty;
        if (declared.Contains("IResult", StringComparison.Ordinal)
            || lambda.ToString().Contains("IResult", StringComparison.Ordinal))
        {
            return true;
        }

        if (declarator.Initializer is not { } initializer)
        {
            return false;
        }

        var arrow = lambda.DescendantTokens()
            .FirstOrDefault(token => token.IsKind(SyntaxKind.EqualsGreaterThanToken));
        var end = arrow == default ? lambda.SpanStart : arrow.SpanStart;
        var span = TextSpan.FromBounds(initializer.EqualsToken.Span.End, end);
        return declarator.SyntaxTree.GetText().ToString(span).Contains("IResult", StringComparison.Ordinal);
    }

    private static void AddRefusal(Dictionary<string, bool> refusals, string name, bool refuses) =>
        refusals[name] = refusals.TryGetValue(name, out var known) ? known || refuses : refuses;

    private static bool HelperRefuses(MethodDeclarationSyntax method)
    {
        var type = method.ReturnType.ToString();
        return type.Contains("IResult", StringComparison.Ordinal)
            || OwnedNodes(method).Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
    }
}

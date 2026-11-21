using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architecture.Tests;

/// <summary>
/// Residual shapes the route-tree scan cannot see from a fluent chain alone: a Map result stored in a variable, a
/// convention collection reached on the group, a route registered by a helper that is not named Map, a handler
/// overloaded in another partial part, a using alias that renames a compared type, and the host's composer chain.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private const string ApiHostPath = "src/Evidgate.Api/ApiHost.cs";

    // The host's filters on the composer result are pinned by name and order: an extra, missing or reordered filter
    // changes every authoring route at once, so the exact set is the allow-list and anything else is refused.
    private static readonly string[] ComposerResultFilters = ["TenantResolutionFilter", "ResultEndpointFilter"];

    private static readonly string[] RouteBuilderTypes = ["RouteGroupBuilder", "IEndpointRouteBuilder"];

    private static IEnumerable<string> ConventionOnStoredBuilderErrors(SyntaxNode root, string path)
    {
        var builders = RouteBuilderVariables(root);
        var stored = StoredBuilderVariables(root);
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Expression is not IdentifierNameSyntax receiver
                || !stored.Contains(receiver.Identifier.ValueText)
                || access.Parent is not InvocationExpressionSyntax call
                || call.Expression != access)
            {
                continue;
            }

            var name = access.Name.Identifier.ValueText;
            if (AllowedChainCalls.Contains(name, StringComparer.Ordinal)
                || name.StartsWith("Map", StringComparison.Ordinal)
                || IsAcceptsMetadata(name, call))
            {
                continue;
            }

            yield return $"{path}:{Line(call)}: {name} on a stored route builder can add behaviour before the handler guard";
        }

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (IsRouteTreeCall(call) || !CarriesBuilderMap(call, builders))
            {
                continue;
            }

            yield return $"{path}:{Line(call)}: a stored route builder result passed to {CallName(call)} leaves the fluent chain before the handler guard";
        }
    }

    private static bool CarriesBuilderMap(InvocationExpressionSyntax call, HashSet<string> builders) =>
        call.ArgumentList.Arguments.Any(argument => argument.Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(inner => IsBuilderMapCall(inner, builders)));

    private static bool IsBuilderMapCall(InvocationExpressionSyntax call, HashSet<string> builders) =>
        CallName(call).StartsWith("Map", StringComparison.Ordinal)
        && call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
        && builders.Contains(receiver.Identifier.ValueText);

    // The calls a route tree may carry: registration, the declared metadata chain and the permitted null guard.
    private static bool IsRouteTreeCall(InvocationExpressionSyntax call)
    {
        var name = CallName(call);
        return name.StartsWith("Map", StringComparison.Ordinal)
            || AllowedChainCalls.Contains(name, StringComparer.Ordinal)
            || IsAcceptsMetadata(name, call)
            || IsNullGuard(call);
    }

    // A Map result assigned to a variable leaves the fluent chain, so the chain checks never see a later call on it.
    // Parameters are found separately because a bare convention call on the group is refused by the non-Map check,
    // while only a variable holding a Map result can carry a fluent call the chain checks cannot reach.
    private static HashSet<string> RouteBuilderVariables(SyntaxNode root)
    {
        var names = ParameterBuilderNames(root);
        var declarations = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var declaration in declarations)
            {
                if (names.Contains(declaration.Identifier.ValueText) || declaration.Initializer is not { } initializer)
                {
                    continue;
                }

                var value = initializer.Value;
                var stored = value is IdentifierNameSyntax identifier && names.Contains(identifier.Identifier.ValueText);
                var mapped = value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                    .Any(call => CallName(call).StartsWith("Map", StringComparison.Ordinal));
                if (stored || mapped)
                {
                    changed |= names.Add(declaration.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    // Builder names that a convention call can reach through a variable: locals holding a Map result or copying one.
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

    private static IEnumerable<string> FilterFactoriesErrors(SyntaxNode root, string path) =>
        root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Name.Identifier.ValueText is "FilterFactories" or "Conventions")
            .Select(access => $"{path}:{Line(access)}: the route group's convention collection can add a filter to every route under it");

    // A helper that takes or is called on the group can register a route without a Map name, so any non-Map call
    // passed a route builder is refused. The over-approximation still keeps every real composer, which is a Map* method.
    private static IEnumerable<string> NonMapRegistrationErrors(SyntaxNode root, string path)
    {
        var builders = RouteBuilderVariables(root);
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = CallName(call);
            if (name.StartsWith("Map", StringComparison.Ordinal)
                || AllowedChainCalls.Contains(name, StringComparer.Ordinal)
                || FilterCalls.Contains(name, StringComparer.Ordinal)
                || IsAcceptsMetadata(name, call)
                || IsNullGuard(call))
            {
                continue;
            }

            if (BuildersInArguments(call, builders) || BuilderReceiver(call, builders) is not null)
            {
                yield return $"{path}:{Line(call)}: {name} reaches the route group through a non-Map helper that can register a route";
            }
        }
    }

    private static bool BuildersInArguments(InvocationExpressionSyntax call, HashSet<string> builders) =>
        call.ArgumentList.Arguments.Any(argument => argument.Expression is IdentifierNameSyntax identifier
            && builders.Contains(identifier.Identifier.ValueText));

    private static bool IsNullGuard(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "ArgumentNullException" },
            Name.Identifier.ValueText: "ThrowIfNull",
        };

    // Covers obj.Post(...) and obj?.Post(...) on a stored group or a builder parameter; a call on another expression
    // (a chain result, a member of a context) is left to the chain checks.
    private static string? BuilderReceiver(InvocationExpressionSyntax call, HashSet<string> builders) => call.Expression switch
    {
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } when builders.Contains(receiver.Identifier.ValueText) =>
            receiver.Identifier.ValueText,
        MemberBindingExpressionSyntax { Parent.Parent: ConditionalAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } }
            when builders.Contains(receiver.Identifier.ValueText) => receiver.Identifier.ValueText,
        _ => null,
    };

    // A handler is resolved inside its own declaration only, so an overload in another part of the partial type is
    // invisible to it; the same namespace and type name across the scanned universe is what identifies the parts.
    private static IEnumerable<string> PartialOverloadErrors(
        (string Path, string Text) file,
        SyntaxNode root,
        (string Path, string Text)[] universe)
    {
        var declarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToArray();
        foreach (var type in declarations)
        {
            var typeName = type.Identifier.ValueText;
            var space = NamespaceOf(type);
            var used = HandlerNames(type);
            foreach (var handler in used)
            {
                var sibling = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .Where(other => other != type
                        && other.Identifier.ValueText == typeName
                        && NamespaceOf(other) == space)
                    .SelectMany(other => other.Members.OfType<MethodDeclarationSyntax>())
                    .Any(method => method.Identifier.ValueText == handler);
                var overloaded = sibling || universe
                    .Where(candidate => candidate.Path != file.Path
                        && candidate.Text.Contains("partial", StringComparison.Ordinal)
                        && candidate.Text.Contains(handler, StringComparison.Ordinal)
                        && candidate.Text.Contains(typeName, StringComparison.Ordinal))
                    .Any(candidate => DeclaresHandler(Parse(candidate.Text), space, typeName, handler));
                if (overloaded)
                {
                    yield return $"{file.Path}:{Line(type)}: {handler} is also declared in another part of partial type {typeName}, so the handler does not resolve to one declaration";
                }
            }
        }
    }

    private static IEnumerable<string> HandlerNames(TypeDeclarationSyntax type) =>
        type.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => CallName(call).StartsWith("Map", StringComparison.Ordinal))
            .Select(call => (call.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression as IdentifierNameSyntax)?.Identifier.ValueText)
            .OfType<string>()
            .Distinct();

    private static string NamespaceOf(SyntaxNode node) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;

    private static bool DeclaresHandler(SyntaxNode root, string space, string typeName, string handler) =>
        root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == typeName && NamespaceOf(candidate) == space)
            .SelectMany(candidate => candidate.Members.OfType<MethodDeclarationSyntax>())
            .Any(method => method.Identifier.ValueText == handler);

    // Type text is compared strictly, so a using alias can rename a bound parameter type or a compared type and slip
    // past the allow-list; any alias in a scanned route file is refused rather than resolved.
    private static IEnumerable<string> UsingAliasErrors(SyntaxNode root, string path) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(directive => directive.Alias is not null)
            .Select(directive => $"{path}:{Line(directive)}: a using alias renames a type the route pin compares by text");

    // The composer result is the one place the host may attach filters, because it is the root of the whole tree:
    // exactly the pinned filters in order, no other call, no stored copy used later.
    private static List<string> ComposerResultFilterErrors(SyntaxNode root, string path)
    {
        var errors = new List<string>();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(candidate => CallName(candidate) == "MapAuthoringEndpoints"))
        {
            var applied = new List<string>();
            var outer = ChainEnd(call, path, applied, errors);
            if (!applied.SequenceEqual(ComposerResultFilters, StringComparer.Ordinal))
            {
                errors.Add($"{path}:{Line(call)}: the composer result must add exactly {string.Join(", ", ComposerResultFilters)}, got {string.Join(", ", applied)}");
            }

            AddStoredComposerErrors(root, outer, path, errors);
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
        call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
            && generic.TypeArgumentList.Arguments is [IdentifierNameSyntax type]
                ? type.Identifier.ValueText
                : null;

    private static void AddStoredComposerErrors(SyntaxNode root, SyntaxNode end, string path, List<string> errors)
    {
        if (end.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
        {
            return;
        }

        var name = declarator.Identifier.ValueText;
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Expression is IdentifierNameSyntax receiver
                && receiver.Identifier.ValueText == name
                && access.Parent is InvocationExpressionSyntax call
                && call.Expression == access)
            {
                errors.Add($"{path}:{Line(call)}: {access.Name.Identifier.ValueText} on a stored composer result can change every authoring route");
            }
        }
    }

    // One level and same file only: a helper of the same type called before the send that can return a Results
    // value or throw is refused, because either answers the caller before the pipeline authorises it. Static
    // ThrowIf*/Guard helpers are refused by name; ArgumentNullException.ThrowIfNull stays the permitted null check.
    private static IEnumerable<string> HelperRefusalErrors(MethodDeclarationSyntax method, string? mediator, string path, string name)
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

    private static readonly HashSet<SyntaxNode> NoSpine = [];

    private static IEnumerable<string> Refusals(
        SyntaxNode node,
        MethodDeclarationSyntax owner,
        string path,
        string name,
        HashSet<SyntaxNode> sendSpine)
    {
        var helpers = SameTypeHelpers(owner);
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

            if (call.Expression is IdentifierNameSyntax callee
                && helpers.TryGetValue(callee.Identifier.ValueText, out var helper)
                && HelperRefuses(helper))
            {
                yield return $"{path}:{Line(call)}: {name} calls helper refusal {callee.Identifier.ValueText} before mediator.Send, so the caller sees a failure that bypasses authorisation";
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

    private static bool IsStaticThrowHelper(InvocationExpressionSyntax call)
    {
        var name = CallName(call);
        var receiver = call.Expression is MemberAccessExpressionSyntax access ? access.Expression.ToString() : string.Empty;
        if (receiver == "ArgumentNullException" && name == "ThrowIfNull")
        {
            return false;
        }

        return receiver is "Guard" or "ArgumentException" or "ArgumentNullException" or "ArgumentOutOfRangeException"
            || (receiver.Length == 0 && name.StartsWith("ThrowIf", StringComparison.Ordinal));
    }

    private static Dictionary<string, MethodDeclarationSyntax> SameTypeHelpers(MethodDeclarationSyntax owner) =>
        owner.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Members.OfType<MethodDeclarationSyntax>()
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal)
        ?? new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);

    private static bool HelperRefuses(MethodDeclarationSyntax method)
    {
        var type = method.ReturnType.ToString();
        return type.Contains("IResult", StringComparison.Ordinal)
            || OwnedNodes(method).Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
    }
}

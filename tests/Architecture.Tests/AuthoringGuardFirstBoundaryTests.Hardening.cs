using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Architecture.Tests;

/// <summary>
/// Residual shapes the route-tree scan cannot see from a fluent chain alone: a Map result stored in a variable or
/// copied through an assignment, a convention collection reached on the group (also through conditional access), a
/// route registered by a helper that is not named Map, a handler overloaded in another partial part, a using alias
/// that renames a compared type anywhere in production, and the host's composer chain. The builder-identity,
/// composer and helper-refusal helpers live in their own parts.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    // Carried limits, deliberate: the filter allow-list is matched by name; a helper of another type, or a helper of
    // a helper, is not resolved; there is no dominance analysis, no parse diagnostics, and an EndpointDataSource or
    // reflection registration is not followed. Each of those can hide a route tree from this scan.
    private const string ApiHostPath = "src/Evidgate.Api/ApiHost.cs";

    // The host's filters on the composer result are pinned by name and order: an extra, missing or reordered filter
    // changes every authoring route at once, so the exact set is the allow-list and anything else is refused.
    private static readonly string[] ComposerResultFilters = ["TenantResolutionFilter", "ResultEndpointFilter"];

    private static readonly string[] RouteBuilderTypes = ["RouteGroupBuilder", "IEndpointRouteBuilder"];

    // Aliases that would rename a type the pin compares by text, anywhere in production sources: the host, a global
    // using and a route file are all covered, and the real solution is expected to have none. The pattern is only a
    // prefilter, so a file with no such alias is never parsed twice.
    private static readonly string[] ProtectedAliasNames =
        ["Guid", "HttpContext", "IMediator", "CancellationToken", "TenantResolutionFilter", "ResultEndpointFilter"];

    private static readonly Regex ProtectedAliasPattern = new(
        @"\busing\s+(?:global\s+)?(?:Guid|HttpContext|IMediator|CancellationToken|TenantResolutionFilter|ResultEndpointFilter)\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A Map result stored in a variable leaves the fluent chain, so every later call on it is judged here: the whole
    // continuation chain is walked, not just a call directly on the variable, and parentheses, `!`, casts, conditional
    // access and nested Map results are looked through on the receiver.
    private static IEnumerable<string> ConventionOnStoredBuilderErrors(SyntaxNode root, string path)
    {
        var builders = RouteBuilderVariables(root);
        var stored = StoredBuilderVariables(root);
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is IdentifierNameSyntax
                || ChainRootName(call.Expression) is not { } rootName
                || !stored.Contains(rootName)
                || IsAllowedContinuation(call))
            {
                continue;
            }

            yield return $"{path}:{Line(call)}: {CallName(call)} on a stored route builder can add behaviour before the handler guard";
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

    private static bool IsAllowedContinuation(InvocationExpressionSyntax call)
    {
        var name = CallName(call);
        return name.StartsWith("Map", StringComparison.Ordinal)
            || AllowedChainCalls.Contains(name, StringComparer.Ordinal)
            || IsAcceptsMetadata(name, call)
            || IsNullGuard(call);
    }

    // The convention collections of a route group add a filter to every route under it. Only a tracked route builder,
    // a Map result over one or a member chain rooted at one can reach them, so an unrelated collection stays green;
    // the conditional-access form `group?.FilterFactories` is a member binding, not a member access.
    private static IEnumerable<string> FilterFactoriesErrors(SyntaxNode root, string path)
    {
        var builders = RouteBuilderVariables(root);
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (IsConventionCollection(access.Name.Identifier.ValueText) && IsTrackedReceiver(access.Expression, builders))
            {
                yield return ConventionCollectionError(access, path);
            }
        }

        foreach (var binding in root.DescendantNodes().OfType<MemberBindingExpressionSyntax>())
        {
            if (IsConventionCollection(binding.Name.Identifier.ValueText)
                && ConditionalOf(binding) is { } conditional
                && IsTrackedReceiver(conditional.Expression, builders))
            {
                yield return ConventionCollectionError(binding, path);
            }
        }
    }

    private static bool IsConventionCollection(string name) => name is "FilterFactories" or "Conventions";

    private static string ConventionCollectionError(SyntaxNode node, string path) =>
        $"{path}:{Line(node)}: the route group's convention collection can add a filter to every route under it";

    // A helper that takes or is called on the group can register a route without a Map name, so any non-Map call
    // passed a route builder is refused. A bare builder (after parentheses, `!`, a cast, a conditional access or a
    // branch of a conditional or coalescing value) is enough; the over-approximation still keeps every real
    // composer, which is a Map* method, and the map-carrying argument case is reported by the stored-builder check.
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

            if (BuildersInArguments(call, builders) || CallsOnBareBuilder(call, builders))
            {
                yield return $"{path}:{Line(call)}: {name} reaches the route group through a non-Map helper that can register a route";
            }
        }
    }

    // A handler is resolved inside its own declaration only, so an overload in another part of the partial type is
    // invisible to it; the full namespace (nested block namespaces composed, not just the innermost) and type name
    // across the scanned universe is what identifies the parts.
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

    // The whole enclosing namespace chain, outermost first: `namespace N { namespace Inner { … } }` and
    // `namespace N.Inner;` are the same namespace and their partial parts must match.
    private static string NamespaceOf(SyntaxNode node) =>
        string.Join(
            ".",
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(space => space.Name.ToString()));

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

    // The same aliases in any other production file, the host included, because a global or host alias renames a
    // compared type for every file that sees it. Carried limit: only these six names, and only a `using X = …` form.
    private static IEnumerable<string> ProtectedAliasErrors(SyntaxNode root, string path) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(directive => directive.Alias is { } alias
                && ProtectedAliasNames.Contains(alias.Name.ToString(), StringComparer.Ordinal))
            .Select(directive =>
                $"{path}:{Line(directive)}: a using alias renames {directive.Alias!.Name}, a type the route pin compares by text");
}

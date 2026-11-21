using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Scope of the Authoring write-route pin: which files are read, which composers are followed, and the checks on
/// delegate parameters and route filter chains that run before the guard-first rule.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    // Endpoint filters and builder conventions run before the delegate, so none may sit on an authoring route tree.
    private static readonly string[] FilterCalls = ["AddEndpointFilter", "AddEndpointFilterFactory"];
    private static readonly string[] ChainCalls = ["WithMetadata", "Add", "Finally"];
    private static readonly string[] AllowedParameterTypes = ["Guid", "HttpContext", "IMediator", "CancellationToken"];

    private static readonly string[] PathMaps =
        ["MapPost", "MapPut", "MapPatch", "MapDelete", "MapMethods", "MapGet", "MapHead", "MapOptions", "MapGroup"];

    private sealed class ScanState
    {
        public ScanState((string Path, string Text)[] files, (string Path, string Text)[] universe)
        {
            Universe = universe;
            Queue = new Queue<(string Path, string Text)>(files);
            Seen = new HashSet<string>(files.Select(file => file.Path), StringComparer.Ordinal);
        }

        public (string Path, string Text)[] Universe { get; }

        public Queue<(string Path, string Text)> Queue { get; }

        public HashSet<string> Seen { get; }

        public List<string> Errors { get; } = [];
    }

    private static SyntaxNode Parse(string text)
    {
        var token = TestContext.Current.CancellationToken;
        return CSharpSyntaxTree.ParseText(text, cancellationToken: token).GetRoot(token);
    }

    /// <summary>Every Authoring endpoint file plus any other production file that registers a route under the authoring path.</summary>
    private static (string Path, string Text)[] AuthoringRouteFiles((string Path, string Text)[] universe) =>
        universe.Where(file => file.Path.StartsWith(Endpoints, StringComparison.Ordinal)
                || (file.Text.Contains("authoring", StringComparison.OrdinalIgnoreCase) && NamesAuthoringPath(Parse(file.Text))))
            .ToArray();

    private static bool NamesAuthoringPath(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
        {
            var name = CallName(call);
            if (!PathMaps.Contains(name, StringComparer.Ordinal) && !IsComposer(name))
            {
                return false;
            }

            // The callee's own name is not evidence: a composer called MapAuthoringEndpoints on a plain builder is not a path.
            return Regex.IsMatch(call.ToString().Replace(name, string.Empty, StringComparison.Ordinal), "authoring", RegexOptions.IgnoreCase);
        });

    private static bool IsComposer(string name) =>
        name.StartsWith("Map", StringComparison.Ordinal) && name.EndsWith("Endpoints", StringComparison.Ordinal);

    /// <summary>Queues the declaring files of a composer so its routes are held to the same rules as the folder's.</summary>
    private static void Follow(InvocationExpressionSyntax call, string name, string path, ScanState state)
    {
        var declaring = state.Universe
            .Where(file => file.Text.Contains(name + "(", StringComparison.Ordinal)
                && Parse(file.Text).DescendantNodes().OfType<MethodDeclarationSyntax>().Any(method => method.Identifier.ValueText == name))
            .ToArray();
        if (declaring.Length == 0)
        {
            state.Errors.Add($"{path}:{Line(call)}: composer {name} has no declaration in production sources and is refused");
            return;
        }

        foreach (var file in declaring.Where(file => state.Seen.Add(file.Path)))
        {
            state.Queue.Enqueue(file);
        }
    }

    private static string[] UnnamedCallErrors(InvocationExpressionSyntax call, string path) =>
        // An invocation whose callee the scanner cannot name is refused rather than assumed to be a read
        // route, so a conditional-access or cast registration cannot carry an unguarded write past it.
        Regex.IsMatch(call.ToString(), @"\bMap\w*")
            ? [$"{path}:{Line(call)}: unrecognized Map call shape is refused rather than assumed to be a read route"]
            : [];

    private static List<string> ParameterErrors(MethodDeclarationSyntax method, string path, string name)
    {
        var errors = new List<string>();
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var type = parameter.Type?.ToString() ?? string.Empty;
            if (parameter.AttributeLists.Count > 0 || !AllowedParameterTypes.Contains(type, StringComparer.Ordinal))
            {
                errors.Add($"{path}:{Line(parameter)}: {name} parameter '{parameter}' can bind request data before the guard; only route ids, HttpContext, IMediator and CancellationToken are allowed");
            }
        }

        return errors;
    }

    private static List<string> ChainErrors(InvocationExpressionSyntax call, string path)
    {
        var errors = new List<string>();
        SyntaxNode node = call;
        while (node.Parent is MemberAccessExpressionSyntax access
            && access.Parent is InvocationExpressionSyntax outer
            && outer.Expression == access)
        {
            var name = access.Name.Identifier.ValueText;
            if (ChainCalls.Contains(name, StringComparer.Ordinal) && !IsAcceptsMetadata(name, outer))
            {
                errors.Add($"{path}:{Line(outer)}: {name} on an authoring route chain can add behaviour before the handler guard");
            }

            node = outer;
        }

        return errors;
    }

    private static bool IsAcceptsMetadata(string name, InvocationExpressionSyntax call) =>
        name == "WithMetadata"
        && call.ArgumentList.Arguments is [{ Expression: ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.ValueText: "AcceptsMetadata" } } }];
}

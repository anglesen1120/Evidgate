using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every Authoring write delegate authenticates its tenant before inspecting request data, and every read
/// delegate authenticates before it may refuse a query: the 403 must come from the pipeline, so no return
/// may run before the handler's mediator send. Route discovery, not helper names, keeps both inside the boundary.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private const string Endpoints = "src/Modules/Evidgate.Modules.Authoring/Endpoints/";
    private static readonly (string TypeMethod, string Reason)[] Exemptions = [];

    private static readonly string[] ReadRouteMaps = ["MapGet", "MapHead", "MapOptions"];

    // Every route map that never carries a write: the read registrations plus the group composer.
    private static readonly string[] ReadMaps = [.. ReadRouteMaps, "MapGroup"];

    private static readonly string[] Expected =
    [
        "AiSettingsEndpoints.SaveAsync",
        "AuthoringChangeEndpoints.QueueAsync",
        "AuthoringChangeEndpoints.DiscardAsync",
        "AuthoringChangeEndpoints.ApplyAsync",
        "AuthoringChecklistEndpoints.CreateAsync",
        "AuthoringChecklistEndpoints.UpdateAsync",
        "AuthoringChecklistEndpoints.DeleteAsync",
        "AuthoringManualCaseEndpoints.CreateAsync",
        "AuthoringManualCaseEndpoints.PatchAsync",
        "AuthoringManualCaseEndpoints.DeleteAsync",
        "AuthoringManualCaseEndpoints.SetStateAsync",
        "AuthoringManualCaseEndpoints.MoveAsync",
        "AuthoringManualCaseEndpoints.LinkAsync",
        "AuthoringManualCaseEndpoints.UnlinkAsync",
        "AuthoringNodeEndpoints.CreateNodeAsync",
        "AuthoringNodeEndpoints.UpdateNodeAsync",
        "AuthoringNodeEndpoints.ReorderNodesAsync",
        "AuthoringNodeEndpoints.DeleteNodeAsync",
        "AuthoringRestoreEndpoints.RestoreNodeAsync",
        "AuthoringRestoreEndpoints.RestoreChecklistItemAsync",
        "AuthoringRestoreEndpoints.RestoreManualCaseAsync",
        "AuthoringRestoreEndpoints.RestoreVersionAsync",
        "AuthoringImportEndpoints.ImportAsync",
    ];

    [Fact]
    public void AuthoringWriteDelegates_AllRegisteredWrites_GuardFirst()
    {
        var universe = SolutionLayout.ProductionSources().ToArray();
        var files = AuthoringRouteFiles(universe);

        files.Count(file => file.Path.StartsWith(Endpoints, StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(8, "the scan must read the Authoring endpoint files");

        var found = new List<string>();
        Scan(files, found, null, universe).ShouldBeEmpty();
        found.Order(StringComparer.Ordinal).ShouldBe(Expected.Order(StringComparer.Ordinal));
    }

    private static string[] Scan(
        (string Path, string Text)[] files,
        List<string> found,
        (string TypeMethod, string Reason)[]? exemptions = null,
        (string Path, string Text)[]? universe = null,
        bool reads = false,
        (string TypeMethod, string Reason)[]? readExemptions = null)
    {
        exemptions ??= Exemptions;
        readExemptions ??= ReadGuardExemptions;
        var state = new ScanState(files, universe ?? files);
        while (state.Queue.Count > 0)
        {
            ScanFile(state.Queue.Dequeue(), found, exemptions, state, reads, readExemptions);
        }

        // Read exemptions are checked by the real-solution fact, which is the only scan with that universe:
        // a synthetic fixture cannot report a real production delegate as stale.
        if (!reads)
        {
            foreach (var (name, _) in exemptions.Where(exemption => !found.Contains(exemption.TypeMethod, StringComparer.Ordinal)))
            {
                state.Errors.Add($"{Endpoints}:1: stale exemption {name} is no longer a write delegate");
            }
        }

        return state.Errors.ToArray();
    }

    private static void ScanFile(
        (string Path, string Text) file,
        List<string> found,
        (string TypeMethod, string Reason)[] exemptions,
        ScanState state,
        bool reads,
        (string TypeMethod, string Reason)[] readExemptions)
    {
        var (path, text) = file;
        var root = Parse(text);
        var conditional = ConditionalSourceText.FirstLine(root);
        if (conditional > 0)
        {
            state.Errors.Add($"{path}:{conditional}: conditionally compiled text in an endpoint file hides code from the pin");
        }

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ScanCall(call, path, found, exemptions, state, reads, readExemptions);
        }
    }

    private static void ScanCall(
        InvocationExpressionSyntax call,
        string path,
        List<string> found,
        (string TypeMethod, string Reason)[] exemptions,
        ScanState state,
        bool reads,
        (string TypeMethod, string Reason)[] readExemptions)
    {
        var map = CallName(call);
        if (map.Length == 0)
        {
            state.Errors.AddRange(UnnamedCallErrors(call, path));
            return;
        }

        if (FilterCalls.Contains(map, StringComparer.Ordinal))
        {
            state.Errors.Add($"{path}:{Line(call)}: {map} on an authoring route tree runs before the handler guard");
            return;
        }

        if (map == "MapGroup" || IsComposer(map))
        {
            state.Errors.AddRange(ChainErrors(call, path));
        }

        if (IsComposer(map))
        {
            Follow(call, map, path, state);
        }

        if (reads)
        {
            ScanReadCall(call, map, path, found, state.Errors, readExemptions);
            return;
        }

        ScanWriteCall(call, map, path, found, exemptions, state.Errors);
    }

    // A read route is any route that cannot write: HEAD and OPTIONS take the guard-first rule beside GET.
    private static void ScanReadCall(
        InvocationExpressionSyntax call,
        string map,
        string path,
        List<string> found,
        List<string> errors,
        (string TypeMethod, string Reason)[] exemptions)
    {
        if (!ReadRouteMaps.Contains(map, StringComparer.Ordinal))
        {
            return;
        }

        errors.AddRange(ChainErrors(call, path));
        ScanRead(call, path, found, errors, exemptions);
    }

    private static void ScanWriteCall(
        InvocationExpressionSyntax call,
        string map,
        string path,
        List<string> found,
        (string TypeMethod, string Reason)[] exemptions,
        List<string> errors)
    {
        if (!map.StartsWith("Map", StringComparison.Ordinal)
            || ReadMaps.Contains(map, StringComparer.Ordinal)
            || map.EndsWith("Endpoints", StringComparison.Ordinal))
        {
            return;
        }

        if (map is not ("MapPost" or "MapPut" or "MapPatch" or "MapDelete"))
        {
            errors.Add($"{path}:{Line(call)}: unsupported write-capable {map} call");
            return;
        }

        errors.AddRange(ChainErrors(call, path));
        ScanWrite(call, path, found, exemptions, errors);
    }

    private static void ScanWrite(
        InvocationExpressionSyntax call,
        string path,
        List<string> found,
        (string TypeMethod, string Reason)[] exemptions,
        List<string> errors)
    {
        if (ResolveHandler(call, path, "write", errors) is not { } resolved)
        {
            return;
        }

        var (method, name) = resolved;
        found.Add(name);
        errors.AddRange(ParameterErrors(method, path, name));
        var guarded = GuardFirst(method);
        var exemption = exemptions.Where(candidate => candidate.TypeMethod == name).ToArray();
        if (exemption.Length != 0)
        {
            if (guarded || exemption.Length != 1 || string.IsNullOrWhiteSpace(exemption[0].Reason))
            {
                errors.Add($"{path}:{Line(method)}: stale or invalid exemption {name}");
            }
        }
        else if (!guarded)
        {
            errors.Add($"{path}:{Line(method)}: {name} must have RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices) first");
        }
    }

    private static (MethodDeclarationSyntax Method, string Name)? ResolveHandler(
        InvocationExpressionSyntax call,
        string path,
        string kind,
        List<string> errors)
    {
        var type = call.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var argument = call.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression;
        if (type is null || argument is not IdentifierNameSyntax identifier || IsAlias(call, identifier.Identifier.ValueText))
        {
            errors.Add($"{path}:{Line(call)}: {kind} handler must be a plain same-type method group, not a lambda, member or alias");
            return null;
        }

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == identifier.Identifier.ValueText).ToArray();
        if (methods.Length != 1)
        {
            errors.Add($"{path}:{Line(call)}: {identifier.Identifier.ValueText} resolves to {methods.Length} same-type declarations, expected one");
            return null;
        }

        var method = methods[0];
        return (method, $"{type.Identifier.ValueText}.{method.Identifier.ValueText}");
    }

    private static bool IsAlias(InvocationExpressionSyntax call, string name)
    {
        foreach (var ancestor in call.Ancestors())
        {
            switch (ancestor)
            {
                case SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax or AnonymousMethodExpressionSyntax:
                    if (HasParameter(ancestor, name))
                    {
                        return true;
                    }

                    break;
                case LocalFunctionStatementSyntax local:
                    if (local.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == name))
                    {
                        return true;
                    }

                    break;
                case MethodDeclarationSyntax method:
                    return method.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == name)
                        || method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(variable => variable.Identifier.ValueText == name)
                        || method.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any(localFunction => localFunction.Identifier.ValueText == name);
            }
        }

        return false;
    }

    private static bool HasParameter(SyntaxNode function, string name) => function switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText == name,
        ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == name),
        AnonymousMethodExpressionSyntax { ParameterList: { } list } => list.Parameters.Any(parameter => parameter.Identifier.ValueText == name),
        _ => false,
    };

    private static string CallName(InvocationExpressionSyntax call) => ExpressionName(call.Expression);

    private static string ExpressionName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        ParenthesizedExpressionSyntax parenthesized => ExpressionName(parenthesized.Expression),
        _ => string.Empty,
    };

    private static bool GuardFirst(MethodDeclarationSyntax method)
    {
        var context = method.ParameterList.Parameters
            .FirstOrDefault(parameter => parameter.Type?.ToString().EndsWith("HttpContext", StringComparison.Ordinal) == true)
            ?.Identifier.ValueText;
        var statements = method.Body?.Statements;
        var first = statements?.FirstOrDefault();
        if (first is not null && IsNullCheck(first, context))
        {
            first = statements?.ElementAtOrDefault(1);
        }

        if (context is null
            || first is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }
            || call.Expression is not MemberAccessExpressionSyntax access
            || access.Expression is not IdentifierNameSyntax { Identifier.ValueText: "RawBodyGuard" }
            || access.Name.Identifier.ValueText != "RequireAuthenticatedTenant"
            || call.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        return call.ArgumentList.Arguments[0].Expression is MemberAccessExpressionSyntax services
            && services.Name.Identifier.ValueText == "RequestServices"
            && services.Expression is IdentifierNameSyntax argument
            && argument.Identifier.ValueText == context;
    }

    private static bool IsNullCheck(StatementSyntax statement, string? context) => statement is ExpressionStatementSyntax
    {
        Expression: InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "ArgumentNullException" },
                Name.Identifier.ValueText: "ThrowIfNull",
            },
            ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax target }],
        },
    } && target.Identifier.ValueText == context;

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}

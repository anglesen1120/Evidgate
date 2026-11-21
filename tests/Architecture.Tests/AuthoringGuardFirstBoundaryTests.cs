using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every Authoring write delegate authenticates its tenant before inspecting request data.
/// Route discovery, rather than helper names, keeps new write routes inside the boundary.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private const string Endpoints = "src/Modules/Evidgate.Modules.Authoring/Endpoints/";
    private static readonly (string TypeMethod, string Reason)[] Exemptions = [];

    private static readonly string[] ReadMaps = ["MapGet", "MapHead", "MapOptions", "MapGroup"];

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
        var files = SolutionLayout.ProductionSources()
            .Where(file => file.Path.StartsWith(Endpoints, StringComparison.Ordinal))
            .ToArray();

        files.Length.ShouldBeGreaterThanOrEqualTo(8, "the scan must read the Authoring endpoint files");

        var found = new List<string>();
        Scan(files, found).ShouldBeEmpty();
        found.Order(StringComparer.Ordinal).ShouldBe(Expected.Order(StringComparer.Ordinal));
    }

    private static string[] Scan(
        (string Path, string Text)[] files,
        List<string> found,
        (string TypeMethod, string Reason)[]? exemptions = null)
    {
        exemptions ??= Exemptions;
        var errors = new List<string>();
        foreach (var (path, text) in files)
        {
            var token = TestContext.Current.CancellationToken;
            var root = CSharpSyntaxTree.ParseText(text, cancellationToken: token).GetRoot(token);
            var conditional = ConditionalSourceText.FirstLine(root);
            if (conditional > 0)
            {
                errors.Add($"{path}:{conditional}: conditionally compiled text in an endpoint file hides code from the pin");
            }

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var map = CallName(call);
                if (map.Length == 0)
                {
                    // An invocation whose callee the scanner cannot name is refused rather than assumed to be a read
                    // route, so a conditional-access or cast registration cannot carry an unguarded write past it.
                    if (System.Text.RegularExpressions.Regex.IsMatch(call.ToString(), @"\bMap\w*"))
                    {
                        errors.Add($"{path}:{Line(call)}: unrecognized Map call shape is refused rather than assumed to be a read route");
                    }

                    continue;
                }

                if (!map.StartsWith("Map", StringComparison.Ordinal)
                    || ReadMaps.Contains(map, StringComparer.Ordinal)
                    || map.EndsWith("Endpoints", StringComparison.Ordinal))
                {
                    continue;
                }

                if (map is not ("MapPost" or "MapPut" or "MapPatch" or "MapDelete"))
                {
                    errors.Add($"{path}:{Line(call)}: unsupported write-capable {map} call");
                    continue;
                }

                ScanWrite(call, path, found, exemptions, errors);
            }
        }

        foreach (var (name, _) in exemptions.Where(exemption => !found.Contains(exemption.TypeMethod, StringComparer.Ordinal)))
        {
            errors.Add($"{Endpoints}:1: stale exemption {name} is no longer a write delegate");
        }

        return errors.ToArray();
    }

    private static void ScanWrite(
        InvocationExpressionSyntax call,
        string path,
        List<string> found,
        (string TypeMethod, string Reason)[] exemptions,
        List<string> errors)
    {
        var type = call.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var argument = call.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression;
        if (type is null || argument is not IdentifierNameSyntax identifier || IsAlias(call, identifier.Identifier.ValueText))
        {
            errors.Add($"{path}:{Line(call)}: write handler must be a plain same-type method group, not a lambda, member or alias");
            return;
        }

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == identifier.Identifier.ValueText).ToArray();
        if (methods.Length != 1)
        {
            errors.Add($"{path}:{Line(call)}: {identifier.Identifier.ValueText} resolves to {methods.Length} same-type declarations, expected one");
            return;
        }

        var method = methods[0];
        var name = $"{type.Identifier.ValueText}.{method.Identifier.ValueText}";
        found.Add(name);
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// An Authoring write delegate that validates a precondition (<c>If-Match</c>) or the import query refuses an
/// unauthenticated or tenant-less caller first: <c>RawBodyGuard</c> is its first statement, so the pipeline's own 401
/// is answered before any request header, query value or body byte is parsed.
/// </summary>
/// <remarks>
/// The rule keys on the precondition helpers (<c>IfMatchVersion</c>, <c>IfMatchOrder</c>) and the import query
/// readers (<c>ReadFormat</c>, <c>DryRun</c>) called from a delegate in an Authoring endpoint file, so a new route that
/// uses one cannot skip the guard. Only a leading <c>ArgumentNullException.ThrowIfNull</c> may precede it. The read
/// routes (list, get, export) validate their query before authentication and are outside this rule: carried row
/// "Authoring read routes validate the query before authentication"; the export delegate is exempted by name below and
/// the exemption is itself checked for staleness. Body readers are pinned by <see cref="RawBodyGuardBoundaryTests"/>.
/// </remarks>
public sealed class AuthoringGuardFirstBoundaryTests
{
    private const string Endpoints = "src/Modules/Evidgate.Modules.Authoring/Endpoints/";
    private const string ImportFile = Endpoints + "AuthoringImportEndpoints.cs";
    private const string Export = "AuthoringImportEndpoints.ExportAsync";

    private static readonly string[] Preconditions = ["IfMatchVersion", "IfMatchOrder", "ReadFormat", "DryRun"];

    private static readonly string[] Expected =
    [
        "AuthoringChecklistEndpoints.UpdateAsync",
        "AuthoringChecklistEndpoints.DeleteAsync",
        "AuthoringManualCaseEndpoints.PatchAsync",
        "AuthoringManualCaseEndpoints.DeleteAsync",
        "AuthoringManualCaseEndpoints.SetStateAsync",
        "AuthoringManualCaseEndpoints.MoveAsync",
        "AuthoringNodeEndpoints.UpdateNodeAsync",
        "AuthoringNodeEndpoints.ReorderNodesAsync",
        "AuthoringNodeEndpoints.DeleteNodeAsync",
        "AuthoringRestoreEndpoints.RestoreNodeAsync",
        "AuthoringRestoreEndpoints.RestoreChecklistItemAsync",
        "AuthoringRestoreEndpoints.RestoreManualCaseAsync",
        "AuthoringImportEndpoints.ImportAsync",
    ];

    [Fact]
    public void AuthoringWriteDelegates_ValidatingAPrecondition_GuardFirst()
    {
        var files = SolutionLayout.ProductionSources()
            .Where(file => file.Path.StartsWith(Endpoints, StringComparison.Ordinal)
                && !file.Path.EndsWith("AuthoringEndpointSupport.cs", StringComparison.Ordinal))
            .ToArray();

        files.Length.ShouldBeGreaterThanOrEqualTo(8, "the scan must read the Authoring endpoint files");

        var found = new List<string>();
        Scan(files, found).ShouldBeEmpty();
        found.Order(StringComparer.Ordinal).ShouldBe(Expected.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Scan_GuardFirstAfterTheNullCheck_IsGreen()
    {
        var found = new List<string>();

        Scan([Synthetic("static async Task M(HttpContext context)\n{\nArgumentNullException.ThrowIfNull(context);\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\nSupport.IfMatchVersion(context, out var v);\n}")], found)
            .ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_GuardRemoved_IsRed()
    {
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nSupport.IfMatchVersion(context, out var v);\n}")], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_GuardAfterTheHelper_IsRed()
    {
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nSupport.IfMatchOrder(context, out var v);\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\n}")], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_GuardAfterAnotherStatement_IsRed()
    {
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nvar q = context.Request.Query[\"format\"];\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\nImportFormats.ReadFormat(q);\n}")], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_GuardNestedOrWrongArgument_IsRed()
    {
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nif (context is not null) { RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices); }\nSupport.IfMatchVersion(context, out var v);\n}")], []).Length.ShouldBe(1);
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nRawBodyGuard.RequireAuthenticatedTenant(other.RequestServices);\nImportFormats.DryRun(\"\", out var d);\n}")], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_ImportQueryReadersWithoutGuard_AreRed()
    {
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nImportFormats.DryRun(\"\", out var d);\n}")], []).Length.ShouldBe(1);
        Scan([Synthetic("static async Task M(HttpContext context)\n{\nImportFormats.ReadFormat(\"csv\");\n}")], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_PreconditionCheckInsideADisabledRegion_IsRed()
    {
        Scan([Synthetic("static async Task M(HttpContext context)\n{\n#if NET10_0\nSupport.IfMatchVersion(context, out var v);\n#endif\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\n}")], [])
            .ShouldContain(error => error.Contains("disabled preprocessor region", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_TheNamedReadRouteExemption_StaysDeclaredAndOnlyForExport()
    {
        var source = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, ImportFile));
        var methods = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>();

        methods.Any(method => method.Identifier.ValueText == "ExportAsync" && Calls(method, "ReadFormat")).ShouldBeTrue(
            "the exemption names a delegate that still validates its query before authentication");
        Scan([Synthetic("static async Task ExportAsync(HttpContext context)\n{\nImportFormats.ReadFormat(\"csv\");\n}")], []).Length
            .ShouldBe(1, "the exemption is by type and method name, not by method name alone");
    }

    private static (string Path, string Text) Synthetic(string method) =>
        (Endpoints + "SyntheticRoute.cs", "namespace N;\nstatic class Route\n{\n" + method + "\n}\n");

    private static string[] Scan((string Path, string Text)[] files, List<string> found)
    {
        var errors = new List<string>();
        foreach (var (path, text) in files)
        {
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            foreach (var trivia in root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                if (Preconditions.Any(helper => trivia.ToString().Contains(helper, StringComparison.Ordinal)))
                {
                    errors.Add($"{path}: disabled preprocessor region could hide a precondition check before the guard");
                }
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var name = $"{method.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText}.{method.Identifier.ValueText}";
                if (name == Export && path == ImportFile || !Preconditions.Any(helper => Calls(method, helper)))
                {
                    continue;
                }

                found.Add(name);
                if (!GuardFirst(method))
                {
                    errors.Add($"{path}:{Line(method)}: {name} validates a precondition or the import query before RawBodyGuard is its first statement");
                }
            }
        }

        return errors.ToArray();
    }

    private static string[] Scan((string Path, string Text) file, List<string> found) => Scan([file], found);

    private static bool Calls(MethodDeclarationSyntax method, string helper) => method.DescendantNodes()
        .OfType<InvocationExpressionSyntax>()
        .Any(call => call.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == helper,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == helper,
            _ => false,
        });

    private static bool GuardFirst(MethodDeclarationSyntax method)
    {
        var context = method.ParameterList.Parameters
            .FirstOrDefault(parameter => parameter.Type?.ToString().EndsWith("HttpContext", StringComparison.Ordinal) == true)
            ?.Identifier.ValueText;
        var first = method.Body?.Statements
            .FirstOrDefault(statement => !IsNullCheck(statement, context));

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

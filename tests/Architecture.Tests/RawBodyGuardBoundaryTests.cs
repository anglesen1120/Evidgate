using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every raw request-body read in the Catalog, Authoring, Ingestion, Orchestrator and Policy endpoint files sits
/// behind <c>RawBodyGuard</c>: a caller with no principal or no resolved tenant is answered by the pipeline's own
/// 401 before a body byte is buffered, so the guard has to run before the first read, not inside the parser.
/// </summary>
/// <remarks>
/// A syntax walk without a semantic model. The reader allow-list below is the whole truth: a file or method that
/// reads a body without an entry is red, an entry whose declaration vanished is stale and red, and an entry that
/// stopped reading a body is stale too. Reads are recognised by the request body member (<c>Request.Body</c>,
/// <c>BodyReader</c>, <c>ReadFromJsonAsync</c>, <c>ReadFormAsync</c>) and by the helper names listed here, so
/// reflection, a second repository tree and a body copied by a type the scan does not know are out of reach.
/// </remarks>
public sealed partial class RawBodyGuardBoundaryTests
{
    private const string Modules = "src/Modules/";
    private const string CatalogRules = Modules + "Evidgate.Modules.Catalog/Endpoints/CatalogRequestRules.cs";
    private const string AuthoringSupport = Modules + "Evidgate.Modules.Authoring/Endpoints/AuthoringEndpointSupport.cs";
    private const string AuthoringImport = Modules + "Evidgate.Modules.Authoring/Endpoints/AuthoringImportEndpoints.cs";
    private const string AiSettings = Modules + "Evidgate.Modules.Authoring/Endpoints/AiSettingsPayload.cs";
    private const string Ingestion = Modules + "Evidgate.Modules.Ingestion/Endpoints/IngestionEndpoints.cs";
    private const string Orchestrator = Modules + "Evidgate.Modules.Orchestrator/Endpoints/OrchestratorEndpoints.cs";
    private const string Policy = Modules + "Evidgate.Modules.Policy/Endpoints/PolicyEndpoints.cs";
    private const string GuardFile = "src/BuildingBlocks/Pipeline/Tenancy/RawBodyGuard.cs";

    /// <summary>
    /// The raw-body readers of the five modules. Central readers call the guard themselves; forwarders only delegate;
    /// caller-owned readers keep a route's existing anonymous answer, so each of their call sites must guard first.
    /// <c>ReadBodyAsync</c> in Authoring has two overloads that both delegate.
    /// </summary>
    private static readonly Reader[] Readers =
    [
        new(CatalogRules, "CatalogRequestRules", "ReadAuthenticatedJsonAsync", Protection.Central),
        new(CatalogRules, "CatalogRequestRules", "ReadJsonAsync", Protection.Caller),
        new(AuthoringSupport, "AuthoringEndpointSupport", "ReadAsync", Protection.Central),
        new(AuthoringSupport, "AuthoringEndpointSupport", "ReadBodyAsync", Protection.Forward, 2),
        new(AuthoringSupport, "AuthoringEndpointSupport", "ReadOptionalBodyAsync", Protection.Forward),
        new(AuthoringImport, "AuthoringImportEndpoints", "ReadFileAsync", Protection.Central),
        new(AiSettings, "AiSettingsPayload", "ReadAsync", Protection.Central),
        new(Ingestion, "IngestionEndpoints", "ReadBodyAsync", Protection.Caller),
        new(Orchestrator, "OrchestratorEndpoints", "ReadBodyAsync", Protection.Caller),
        new(Policy, "PolicyEndpoints", "ReadBodyAsync", Protection.Caller),
    ];

    /// <summary>The helper names a raw-body reader is declared under; a new one is a deliberate edit of this list.</summary>
    private static readonly string[] ReaderMethodNames =
    [
        "ReadBodyAsync", "ReadOptionalBodyAsync", "ReadJsonAsync", "ReadFileAsync", "ReadAuthenticatedJsonAsync",
    ];

    /// <summary>A disabled region may compile under another target, so any mention of a guard or a reader is red.</summary>
    private static readonly string[] SuspiciousDisabledText =
    [
        "RawBodyGuard", "RequireAuthenticatedTenant", "RequireResolvedTenant", "IRequestTenantContextAccessor",
        "Request.Body", "BodyReader", "ReadFromJsonAsync", "ReadFormAsync", ".Request",
        .. ReaderMethodNames,
        "\\u", "\\U",
    ];

    [Fact]
    public void ModuleEndpoints_RawBodyReaders_GuardBeforeRead()
    {
        var files = EndpointSources();

        files.Length.ShouldBeGreaterThan(30, "the scan must read every module endpoint file");
        files.Count(file => InBodyScope(file.Path)).ShouldBeGreaterThanOrEqualTo(23);
        Scan(files, Readers).ShouldBeEmpty();
    }

    [Fact]
    public void RawBodyGuard_DeclaresBothGuards()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, GuardFile));

        text.ShouldContain("static void RequireAuthenticatedTenant(HttpContext");
        text.ShouldContain("static void RequireResolvedTenant(HttpContext");
        text.ShouldContain("TenantContextMissingException");
    }

    [Fact]
    public void ModuleEndpoints_NoLocalTenantAccessor()
    {
        var references = EndpointSources()
            .Where(file => file.Text.Contains("IRequestTenantContextAccessor", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        references.ShouldBeEmpty("tenant resolution for a body read has one mechanism, RawBodyGuard");
    }

    private static (string Path, string Text)[] EndpointSources() =>
        SolutionLayout.ProductionSources()
            .Where(file => file.Path.StartsWith(Modules, StringComparison.Ordinal)
                && file.Path.Contains("/Endpoints/", StringComparison.Ordinal))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

    private static readonly string[] BodyModules = ["Catalog", "Authoring", "Ingestion", "Orchestrator", "Policy"];

    private static bool InBodyScope(string path) => BodyModules
        .Any(module => path.StartsWith(Modules + "Evidgate.Modules." + module + "/Endpoints/", StringComparison.Ordinal));

    private static string[] Scan((string Path, string Text)[] files, Reader[] readers)
    {
        var errors = new List<string>();
        var declarations = new List<(string Path, MethodDeclarationSyntax Method)>();
        foreach (var (path, text) in files)
        {
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            CheckFile(path, root, errors);
            if (!InBodyScope(path))
            {
                continue;
            }

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                declarations.AddRange(type.Members.OfType<MethodDeclarationSyntax>().Select(method => (path, method)));
            }
        }

        foreach (var (path, method) in declarations)
        {
            var entry = readers.FirstOrDefault(reader => reader.Matches(path, method));
            if (entry is null && IsReaderName(method.Identifier.ValueText, TypeName(method)))
            {
                errors.Add($"{path}: unlisted reader {TypeName(method)}.{method.Identifier.ValueText}");
            }

            CheckBody(path, method, entry, readers, declarations, errors);
            CheckGuardReferences(path, method, errors);
        }

        foreach (var reader in readers.Where(reader => declarations.Count(item => reader.Matches(item.Path, item.Method)) != reader.Declarations))
        {
            var found = declarations.Count(item => reader.Matches(item.Path, item.Method));
            errors.Add($"{reader.Path}: stale reader allow-list entry {reader.Type}.{reader.Method}; expected {reader.Declarations} declaration(s), found {found}");
        }

        return errors.ToArray();
    }

    private static void CheckFile(string path, SyntaxNode root, List<string> errors)
    {
        var tree = root.SyntaxTree;
        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            errors.Add($"{path}: source does not parse");
        }

        foreach (var trivia in root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            var disabled = trivia.ToString();
            if (SuspiciousDisabledText.Any(needle => disabled.Contains(needle, StringComparison.Ordinal)))
            {
                errors.Add($"{path}: disabled preprocessor region could hide a guard or a body read");
            }
        }

        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.ToString().Contains("RawBodyGuard", StringComparison.Ordinal))
            {
                errors.Add($"{path}: using directive naming RawBodyGuard hides which guard a call reaches");
            }
        }

        if (root.DescendantTokens().Any(token => token.ValueText == "IRequestTenantContextAccessor"))
        {
            errors.Add($"{path}: local tenant accessor reference; tenant resolution for a body read is RawBodyGuard's job");
        }
    }

    private static void CheckBody(
        string path,
        MethodDeclarationSyntax method,
        Reader? entry,
        Reader[] readers,
        List<(string Path, MethodDeclarationSyntax Method)> declarations,
        List<string> errors)
    {
        var raw = method.DescendantNodes().Where(IsRawAccess).ToArray();
        foreach (var node in raw)
        {
            if (entry is null)
            {
                errors.Add($"{path}:{Line(node)}: raw body access outside an allow-listed reader");
            }
            else if (entry.Scope != Protection.Caller && !GuardBefore(node, method))
            {
                errors.Add($"{path}:{Line(node)}: raw body access before the shared guard");
            }
        }

        if (entry is { Scope: Protection.Caller } && raw.Length == 0)
        {
            errors.Add($"{path}: stale reader allow-list entry {entry.Method} no longer reads a body");
        }

        if (entry is { Scope: Protection.Central })
        {
            CheckCentral(path, method, errors);
        }

        if (entry is { Scope: Protection.Forward } && Chain(CallOf(method), method, readers, declarations) is not { Scope: Protection.Central })
        {
            errors.Add($"{path}: reader forwarder {method.Identifier.ValueText} must reach a centrally guarded reader");
        }

        CheckReaderCalls(path, method, readers, errors);
        CheckReaderReferences(path, method, errors);
    }

    private static void CheckReaderReferences(string path, MethodDeclarationSyntax method, List<string> errors)
    {
        foreach (var name in method.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name => IsReaderMethodName(name.Identifier.ValueText)))
        {
            var member = name.Parent as MemberAccessExpressionSyntax;
            var expression = member is not null && member.Name == name ? (ExpressionSyntax)member : name;
            if (expression.Parent is not InvocationExpressionSyntax call || call.Expression != expression)
            {
                errors.Add($"{path}:{Line(name)}: reader referenced without being called directly");
            }
        }
    }

    private static void CheckCentral(string path, MethodDeclarationSyntax method, List<string> errors)
    {
        var guard = GuardStatement(method);
        if (guard is null)
        {
            errors.Add($"{path}: centrally guarded reader {method.Identifier.ValueText} must call RawBodyGuard");
            return;
        }

        var early = method.Body?.Statements
            .Where(statement => statement.Span.End <= guard.SpanStart)
            .FirstOrDefault(statement => statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Any(access => access.Name.Identifier.ValueText == "Request"));
        if (early is not null)
        {
            errors.Add($"{path}:{Line(early)}: request touched before the guard");
        }
    }

    private static void CheckReaderCalls(string path, MethodDeclarationSyntax method, Reader[] readers, List<string> errors)
    {
        foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => IsReaderCall(call, method, readers)))
        {
            var target = Resolve(call, method, readers);
            if (target is null)
            {
                errors.Add($"{path}:{Line(call)}: unresolved reader call");
            }
            else if (target.Scope == Protection.Caller && !GuardBefore(call, method))
            {
                errors.Add($"{path}:{Line(call)}: caller-owned reader {target.Method} invoked before the shared guard");
            }
        }
    }

    private static void CheckGuardReferences(string path, MethodDeclarationSyntax method, List<string> errors)
    {
        foreach (var name in method.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => name.Identifier.ValueText is "RequireAuthenticatedTenant" or "RequireResolvedTenant"))
        {
            var member = name.Parent as MemberAccessExpressionSyntax;
            var call = member?.Parent as InvocationExpressionSyntax;
            if (member is null || member.Name != name || call is null || call.Expression != member || !IsGuard(call))
            {
                errors.Add($"{path}:{Line(name)}: guard referenced without calling RawBodyGuard directly");
            }
        }
    }

    private static Reader? Chain(
        InvocationExpressionSyntax? call,
        MethodDeclarationSyntax method,
        Reader[] readers,
        List<(string Path, MethodDeclarationSyntax Method)> declarations)
    {
        for (var depth = 0; call is not null && depth < 4; depth++)
        {
            var target = Resolve(call, method, readers);
            if (target is null || target.Scope != Protection.Forward)
            {
                return target;
            }

            var next = declarations.FirstOrDefault(item => target.Matches(item.Path, item.Method)
                && item.Method.ParameterList.Parameters.Count == call.ArgumentList.Arguments.Count);
            if (next.Method is null)
            {
                return null;
            }

            method = next.Method;
            call = CallOf(next.Method);
        }

        return null;
    }

    private static InvocationExpressionSyntax? CallOf(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody?.Expression is InvocationExpressionSyntax expression)
        {
            return expression;
        }

        var body = method.Body;
        if (body is null || body.Statements.Count != 1)
        {
            return null;
        }

        return body.Statements[0] is ReturnStatementSyntax { Expression: InvocationExpressionSyntax returned } ? returned : null;
    }

    private static Reader? Resolve(InvocationExpressionSyntax call, MethodDeclarationSyntax method, Reader[] readers)
    {
        var (name, receiver) = CallTarget(call);
        var type = receiver ?? TypeName(method);
        return readers.FirstOrDefault(reader => reader.Type == type && reader.Method == name);
    }

    private static (string Name, string? Receiver) CallTarget(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax simple => (simple.Identifier.ValueText, null),
        MemberAccessExpressionSyntax member => (member.Name.Identifier.ValueText, LastToken(member.Expression)),
        _ => (string.Empty, null),
    };

    private static bool IsReaderCall(InvocationExpressionSyntax call, MethodDeclarationSyntax method, Reader[] readers)
    {
        var (name, receiver) = CallTarget(call);
        if (IsReaderMethodName(name))
        {
            return true;
        }

        return name == "ReadAsync" && readers.Any(reader => reader.Method == "ReadAsync"
            && (receiver == reader.Type || receiver is null && TypeName(method) == reader.Type));
    }

    private static bool IsReaderMethodName(string name) => ReaderMethodNames.Contains(name);

    private static bool IsReaderName(string name, string type) => IsReaderMethodName(name)
        || name == "ReadAsync" && type.EndsWith("Payload", StringComparison.Ordinal);

    private static bool IsRawAccess(SyntaxNode node) => node switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "BodyReader" } => true,
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Body" } member => IsRequest(member.Expression),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ReadFromJsonAsync" or "ReadFormAsync" } } => true,
        _ => false,
    };

    private static bool IsRequest(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Request" } => true,
        IdentifierNameSyntax identifier => RequestAliases(identifier).Contains(identifier.Identifier.ValueText),
        _ => false,
    };

    private static HashSet<string> RequestAliases(IdentifierNameSyntax identifier)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal) { "request", "req", "httpRequest" };
        var method = identifier.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null)
        {
            return aliases;
        }

        foreach (var parameter in method.ParameterList.Parameters.Where(parameter => parameter.Type?.ToString().EndsWith("HttpRequest", StringComparison.Ordinal) == true))
        {
            aliases.Add(parameter.Identifier.ValueText);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var variable in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                var value = variable.Initializer?.Value;
                if (value is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Request" }
                    || value is IdentifierNameSyntax alias && aliases.Contains(alias.Identifier.ValueText))
                {
                    changed |= aliases.Add(variable.Identifier.ValueText);
                }
            }
        }
        while (changed);

        return aliases;
    }

    private static ExpressionStatementSyntax? GuardStatement(MethodDeclarationSyntax method) => method.Body?.Statements
        .OfType<ExpressionStatementSyntax>()
        .FirstOrDefault(statement => statement.Expression is InvocationExpressionSyntax call && IsGuardFor(call, method));

    private static bool GuardBefore(SyntaxNode node, MethodDeclarationSyntax method)
    {
        if (HasNestedFunction(node, method))
        {
            return false;
        }

        var guard = GuardStatement(method);
        return guard is not null && guard.Span.End <= node.SpanStart;
    }

    private static bool IsGuardFor(InvocationExpressionSyntax call, MethodDeclarationSyntax method)
    {
        var context = method.ParameterList.Parameters
            .FirstOrDefault(parameter => parameter.Type?.ToString().EndsWith("HttpContext", StringComparison.Ordinal) == true);
        return IsGuard(call) && context is not null && call.ArgumentList.Arguments.Count == 1
            && call.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax argument
            && argument.Identifier.ValueText == context.Identifier.ValueText;
    }

    private static bool IsGuard(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax access
        && access.Name.Identifier.ValueText is "RequireAuthenticatedTenant" or "RequireResolvedTenant"
        && LastToken(access.Expression) == "RawBodyGuard";

    private static bool HasNestedFunction(SyntaxNode node, MethodDeclarationSyntax method) => node.Ancestors()
        .TakeWhile(parent => parent != method)
        .Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    private static string LastToken(ExpressionSyntax expression) => expression.DescendantTokens().Last().ValueText;

    private static string TypeName(MethodDeclarationSyntax method) => method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? string.Empty;

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private enum Protection { Central, Forward, Caller }

    private sealed record Reader(string Path, string Type, string Method, Protection Scope, int Declarations = 1)
    {
        public bool Matches(string path, MethodDeclarationSyntax method) => path == Path
            && TypeName(method) == Type
            && method.Identifier.ValueText == Method;
    }
}

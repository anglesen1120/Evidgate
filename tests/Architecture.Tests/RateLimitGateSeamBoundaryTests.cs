using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The delegate overload <c>RateLimitGate.AcquireAsync&lt;TState&gt;</c> exists so a test can make the store call fail
/// with an exception the real store cannot be steered into. It is internal and the Api, Worker and Migrator hosts see
/// it through InternalsVisibleTo, so a production route could pass a lambda that always admits and never reach the
/// store. The production callers of that overload are pinned to one file: the limiter overload in the same class.
/// </summary>
/// <remarks>
/// Named static limits (a syntax walk without a semantic model; each is a form this scan does not see):
/// (1) only <c>src/**/*.cs</c> is scanned, so code generated outside it or a second repo tree is out of reach;
/// (2) the allowed file is trusted as a whole: a wrapper added inside ExchangeRateLimiter.cs that forwards a caller's
/// delegate is not seen (only its delegate-call count is pinned, at least one, so no stale entry);
/// (3) a call is told apart by argument count (5 = delegate overload, 7 = limiter overload) and any other count is
/// red, so a new overload is a conscious test edit; a 7-argument call must pass a plain identifier or member access as
/// the limiter, and its file must be on the caller list, so a fifth caller is a deliberate edit;
/// (4) test assemblies are out of the scan by path (only <c>src/</c> is read) because the fault tests legitimately
/// call the delegate overload;
/// (5) reflection that builds the type or method name from pieces, <c>dynamic</c> and source that is not a <c>.cs</c>
/// file are not seen, while a literal naming the type is red.
/// Every other mention of the type (alias, <c>using static</c>, method group, <c>typeof</c>, <c>nameof</c>, a
/// redeclaration) is red outside the allowed file because it cannot be resolved to a call without a compilation.
/// </remarks>
public sealed class RateLimitGateSeamBoundaryTests
{
    private const string GateType = "RateLimitGate";
    private const string MethodName = "AcquireAsync";
    private const int DelegateOverloadArguments = 5;
    private const int LimiterOverloadArguments = 7;

    private const string AllowedFile = "src/Modules/Evidgate.Modules.Identity/Ci/ExchangeRateLimiter.cs";
    private const string RouteFile = "src/Modules/Evidgate.Modules.Identity/Endpoints/SyntheticRoute.cs";
    private const string OtherFile = "src/Modules/Evidgate.Modules.Catalog/Endpoints/SyntheticRoute.cs";

    private static readonly string[] Allowed = [AllowedFile];

    /// <summary>The four routes that spend a budget; a fifth caller is a deliberate edit of this list.</summary>
    private static readonly string[] LimiterCallers =
    [
        "src/Modules/Evidgate.Modules.Identity/Endpoints/CiTokenEndpoints.cs",
        "src/Modules/Evidgate.Modules.Identity/Endpoints/InviteTokenEndpoints.cs",
        "src/Modules/Evidgate.Modules.Identity/Endpoints/LinkEndpoints.cs",
        "src/Modules/Evidgate.Modules.Identity/Endpoints/LoginEndpoints.cs",
    ];

    private readonly ITestOutputHelper output;

    public RateLimitGateSeamBoundaryTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void RealTree_DelegateOverload_IsCalledOnlyFromTheLimiterOverload()
    {
        var files = ProductionSources();
        var report = Scan(files, Allowed, LimiterCallers);

        report.Scanned.ShouldBeGreaterThan(500, "the scan must be reading the real production tree");
        report.Mentions.ShouldBeGreaterThan(0, "the scan must find the type it pins");
        report.Violations.ShouldBeEmpty("the delegate overload may only be called from the limiter overload in ExchangeRateLimiter.cs");
        report.DelegateCalls.ShouldBe([$"{AllowedFile}"]);
        report.LimiterCalls.Distinct().Order(StringComparer.Ordinal).ToArray().ShouldBe(LimiterCallers, "the four routes call the limiter overload");

        output.WriteLine($"scanned {files.Length} files; delegate-overload call sites: {report.DelegateCalls.Length}");
        foreach (var path in report.DelegateCalls)
        {
            output.WriteLine($"  delegate: {path}");
        }

        foreach (var path in report.LimiterCalls.Order(StringComparer.Ordinal))
        {
            output.WriteLine($"  limiter:  {path}");
        }
    }

    [Fact]
    public void AllowedFile_ReallyDeclaresAndCallsTheDelegateOverload()
    {
        var text = File.ReadAllText(Path.Combine(SolutionLayout.RepoRoot, AllowedFile));

        text.ShouldContain("internal static async ValueTask<Error?> AcquireAsync<TState>(");
        text.ShouldContain("Func<TState, CancellationToken, ValueTask<RateDecision>> acquire");
    }

    /// <summary>Each bypass form is red and the finding names the form.</summary>
    [Theory]
    [InlineData("direct call", "RateLimitGate.AcquireAsync(ctx, 1, static (s, t) => default, \"m\", token);", "delegate overload called")]
    [InlineData("lambda that always admits", "RateLimitGate.AcquireAsync(ctx, 0, (s, t) => ValueTask.FromResult(new RateDecision(true, 0)), \"m\", token);", "delegate overload called")]
    [InlineData("generic with explicit type", "RateLimitGate.AcquireAsync<int>(ctx, 0, Admit, \"m\", token);", "delegate overload called")]
    [InlineData("named arguments", "RateLimitGate.AcquireAsync(context: ctx, state: 0, acquire: Admit, refusalMessage: \"m\", cancellationToken: token);", "delegate overload called")]
    [InlineData("fully qualified", "Evidgate.Modules.Identity.Ci.RateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);", "delegate overload called")]
    [InlineData("global qualified", "global::Evidgate.Modules.Identity.Ci.RateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);", "delegate overload called")]
    [InlineData("local function wrapper", "ValueTask<Error?> Wrap() => RateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);", "delegate overload called")]
    [InlineData("method group", "var f = RateLimitGate.AcquireAsync<int>;", "non-call reference")]
    [InlineData("method group passed on", "Use(RateLimitGate.AcquireAsync<int>);", "non-call reference")]
    [InlineData("typeof", "var t = typeof(RateLimitGate);", "non-call reference")]
    [InlineData("nameof", "var n = nameof(RateLimitGate);", "non-call reference")]
    [InlineData("unresolvable argument count", "RateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token, extra);", "unresolved call shape")]
    [InlineData("string literal reflection", "var t = Type.GetType(\"Evidgate.Modules.Identity.Ci.RateLimitGate\");", "string literal")]
    public void Scan_BypassForm_IsRedAndNamed(string form, string statement, string expected)
    {
        var report = Scan([(OtherFile, Wrap(statement))], []);

        report.Violations.ShouldContain(finding => finding.StartsWith(OtherFile, StringComparison.Ordinal) && finding.Contains(expected, StringComparison.Ordinal), form);
    }

    [Fact]
    public void Scan_TypeRedeclaredOutsideTheAllowedFile_IsRed()
    {
        var report = Scan([(OtherFile, "namespace Evidgate.Modules.Identity.Ci { internal static partial class RateLimitGate { } }")], []);

        report.Violations.ShouldContain(finding => finding.Contains("declares RateLimitGate", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UnicodeEscapedGateName_IsStillSeen()
    {
        var report = Scan([(OtherFile, Wrap("R\\u0061teLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);"))], []);

        report.Violations.ShouldContain(finding => finding.Contains("delegate overload called", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#if false\nRateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);\n#endif")]
    [InlineData("#if NET10_0\nRateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);\n#endif")]
    [InlineData("#if NET10_0\nR\\u0061teLimitGate.Go(ctx);\n#endif")]
    [InlineData("#if NET10_0\nvar x = 1;\n#else\nRateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);\n#endif")]
    [InlineData("#if true\nvar x = 1;\n#endif")]
    public void Scan_ConditionalText_IsRed(string statement)
    {
        var report = Scan([(OtherFile, Wrap(statement))], []);

        report.Violations.ShouldContain(finding => finding.Contains("conditionally compiled text", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("// #if NET10_0\n// RateLimitGate.AcquireAsync(ctx, 0, Admit, \"m\", token);")]
    [InlineData("var x = 1;")]
    public void Scan_CommentedOutOrPlainText_RaisesNoConditionalFinding(string statement)
    {
        var report = Scan([(OtherFile, Wrap(statement))], []);

        report.Violations.ShouldNotContain(finding => finding.Contains("conditionally compiled text", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("using alias", "using Gate = Evidgate.Modules.Identity.Ci.RateLimitGate;")]
    [InlineData("using static", "using static Evidgate.Modules.Identity.Ci.RateLimitGate;")]
    [InlineData("global using static", "global using static Evidgate.Modules.Identity.Ci.RateLimitGate;")]
    public void Scan_UsingDirectiveNamingTheGate_IsRed(string form, string directive)
    {
        var report = Scan([(OtherFile, directive + "\nclass Route { }")], []);

        report.Violations.ShouldContain(finding => finding.Contains("using directive", StringComparison.Ordinal), form);
    }

    [Fact]
    public void Scan_UnqualifiedCallInsideARedeclaredGate_IsRed()
    {
        var source = "static class RateLimitGate { static ValueTask<Error?> Go(HttpContext c) => AcquireAsync(c, 0, Admit, \"m\", t); }";

        var report = Scan([(OtherFile, source)], []);

        report.Violations.ShouldContain(finding => finding.Contains("delegate overload called", StringComparison.Ordinal));
        report.Violations.ShouldContain(finding => finding.Contains("declares RateLimitGate", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SourceThatDoesNotParse_IsRed()
    {
        var report = Scan([(OtherFile, "class Route { void M( { RateLimitGate.AcquireAsync(")], []);

        report.Violations.ShouldContain(finding => finding.Contains("does not parse", StringComparison.Ordinal));
    }

    /// <summary>The allowed forms are green: the limiter overload from a route, and the one allowed file.</summary>
    [Fact]
    public void Scan_LimiterOverloadFromARoute_IsGreen()
    {
        var statement = "return RateLimitGate.AcquireAsync(ctx, limiter, RateSurface.Login, key, 1, \"m\", token);";

        var report = Scan([(RouteFile, Wrap(statement))], [], [RouteFile]);

        report.Violations.ShouldBeEmpty();
        report.LimiterCalls.Distinct().ShouldBe([RouteFile]);
        report.DelegateCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("new limiter", "RateLimitGate.AcquireAsync(ctx, new ExchangeRateLimiter(store), S, key, 1, \"m\", token);")]
    [InlineData("lambda", "RateLimitGate.AcquireAsync(ctx, () => fake, S, key, 1, \"m\", token);")]
    [InlineData("cast", "RateLimitGate.AcquireAsync(ctx, (ExchangeRateLimiter)fake, S, key, 1, \"m\", token);")]
    [InlineData("conditional", "RateLimitGate.AcquireAsync(ctx, a ? b : c, S, key, 1, \"m\", token);")]
    [InlineData("named new limiter", "RateLimitGate.AcquireAsync(context: ctx, surface: S, key: key, cost: 1, refusalMessage: \"m\", cancellationToken: token, limiter: new X());")]
    public void Scan_LimiterCallWithNonPlainLimiterArgument_IsRed(string form, string statement)
    {
        var report = Scan([(RouteFile, Wrap(statement))], [], [RouteFile]);

        report.Violations.ShouldContain(finding => finding.Contains("not a plain identifier", StringComparison.Ordinal), form);
    }

    [Theory]
    [InlineData("limiter")]
    [InlineData("this.limiter")]
    [InlineData("options.Limiter")]
    public void Scan_LimiterCallWithPlainLimiterArgument_IsGreen(string limiter)
    {
        var statement = $"return RateLimitGate.AcquireAsync(ctx, {limiter}, S, key, 1, \"m\", token);";

        Scan([(RouteFile, Wrap(statement))], [], [RouteFile]).Violations.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_LimiterCallFromAFileOffTheCallerList_IsRed()
    {
        var statement = "return RateLimitGate.AcquireAsync(ctx, limiter, S, key, 1, \"m\", token);";

        var report = Scan([(OtherFile, Wrap(statement))], [], [RouteFile]);

        report.Violations.ShouldContain(finding => finding.StartsWith(OtherFile, StringComparison.Ordinal) && finding.Contains("not on the caller allow-list", StringComparison.Ordinal));
        report.Violations.ShouldContain(finding => finding.Contains("stale caller allow-list entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_DelegateCallInsideTheAllowedFile_IsGreen()
    {
        var source = "internal static class RateLimitGate { public static ValueTask<Error?> A() => "
            + "AcquireAsync(ctx, (l, s), static (c, t) => c.l.AcquireAsync(c.s, t), \"m\", token); }";

        var report = Scan([(AllowedFile, source)], Allowed);

        report.Violations.ShouldBeEmpty();
        report.DelegateCalls.ShouldBe([AllowedFile]);
    }

    [Fact]
    public void Scan_AllowedFileWithNoDelegateCall_IsStale()
    {
        var report = Scan([(AllowedFile, "internal static class RateLimitGate { }")], Allowed);

        report.Violations.ShouldContain(finding => finding.Contains("stale allow-list entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_AllowedFileMissingFromTheTree_IsStale()
    {
        var report = Scan([(RouteFile, "class Route { }")], Allowed);

        report.Violations.ShouldContain(finding => finding.Contains("stale allow-list entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UnrelatedAcquireAsyncReceivers_AreIgnored()
    {
        var statement = "await limiter.AcquireAsync(surface, key, 1, token); await SeedGuard.AcquireAsync(dataSource);";

        Scan([(OtherFile, Wrap(statement))], []).Violations.ShouldBeEmpty();
    }

    private static string Wrap(string statement) =>
        "using System; class Route { async System.Threading.Tasks.Task M() {\n" + statement + "\n} }";

    private static (string Path, string Text)[] ProductionSources() => [.. SolutionLayout.ProductionSources()];

    private static ScanReport Scan(IEnumerable<(string Path, string Text)> files, string[] allowed, string[]? limiterCallers = null)
    {
        limiterCallers ??= [];
        var state = new ScanState();

        foreach (var (path, text) in files)
        {
            state.Scanned++;
            ScanFile(path, text, allowed.Contains(path), limiterCallers, state);
        }

        foreach (var entry in allowed.Where(entry => !state.DelegateCalls.Contains(entry)))
        {
            state.Violations.Add($"{entry}: stale allow-list entry, the file no longer calls the delegate overload");
        }

        foreach (var entry in limiterCallers.Where(entry => !state.LimiterCalls.Contains(entry)))
        {
            state.Violations.Add($"{entry}: stale caller allow-list entry, the file no longer calls the limiter overload");
        }

        return new ScanReport(state.Violations, state.DelegateCalls.Distinct().Order(StringComparer.Ordinal).ToArray(), state.LimiterCalls, state.Scanned, state.Mentions);
    }

    private static void ScanFile(string path, string text, bool isAllowed, string[] limiterCallers, ScanState state)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        FlagConditionalText(path, tree, state);

        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            state.Violations.Add($"{path}: source does not parse, so the scan cannot rule out a call to {GateType}");

            return;
        }

        foreach (var node in tree.GetRoot().DescendantNodesAndTokens())
        {
            ScanNode(path, node, isAllowed, limiterCallers, state);
        }
    }

    private static void FlagConditionalText(string path, SyntaxTree tree, ScanState state)
    {
        var conditional = ConditionalSourceText.FirstLine(tree.GetRoot());
        if (conditional > 0)
        {
            state.Violations.Add($"{path}:{conditional}: conditionally compiled text hides a call to the gate from the scan");
        }
    }

    private static void ScanNode(string path, SyntaxNodeOrToken node, bool isAllowed, string[] limiterCallers, ScanState state)
    {
        (bool Delegate, bool Counted)? call = null;
        var finding = node.IsToken ? CheckToken(node.AsToken()) : CheckNode(node.AsNode()!, isAllowed, out call);
        var line = node.GetLocation()!.GetLineSpan().StartLinePosition.Line + 1;
        if (node.IsNode && node.AsNode() is IdentifierNameSyntax { Identifier.ValueText: GateType })
        {
            state.Mentions++;
        }

        if (node.IsNode && call is not null)
        {
            (call.Value.Delegate ? state.DelegateCalls : state.LimiterCalls).Add(path);
            if (!call.Value.Delegate && !limiterCallers.Contains(path))
            {
                state.Violations.Add($"{path}:{line}: limiter overload called from a file that is not on the caller allow-list");
            }
        }

        if (finding is not null && !(isAllowed && finding.Allowed))
        {
            state.Violations.Add($"{path}:{line}: {finding.Message}");
        }
    }

    private sealed class ScanState
    {
        public List<string> Violations { get; } = [];

        public List<string> DelegateCalls { get; } = [];

        public List<string> LimiterCalls { get; } = [];

        public int Mentions { get; set; }

        public int Scanned { get; set; }
    }

    private static Finding? CheckToken(SyntaxToken token) =>
        token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken
            && token.ValueText.Contains(GateType, StringComparison.Ordinal)
                ? new Finding($"string literal names {GateType}; a name looked up by reflection reaches the seam unseen", Allowed: false)
                : null;

    private static Finding? CheckNode(SyntaxNode node, bool inAllowedFile, out (bool Delegate, bool Counted)? call)
    {
        call = null;
        switch (node)
        {
            case BaseTypeDeclarationSyntax { Identifier.ValueText: GateType } when !inAllowedFile:
                return new Finding($"declares {GateType} outside the allowed file", Allowed: false);
            case InvocationExpressionSyntax { Expression: SimpleNameSyntax { Identifier.ValueText: MethodName } } invocation
                when invocation.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == GateType):
                return Classify(invocation, out call);
            case IdentifierNameSyntax { Identifier.ValueText: GateType } name:
                return ClassifyMention(name, out call);
            default:
                return null;
        }
    }

    private static Finding? ClassifyMention(IdentifierNameSyntax name, out (bool Delegate, bool Counted)? call)
    {
        call = null;
        if (name.Ancestors().OfType<UsingDirectiveSyntax>().Any())
        {
            return new Finding($"using directive naming {GateType} (alias or using static) hides the call", Allowed: false);
        }

        SyntaxNode qualified = name;
        while (qualified.Parent is MemberAccessExpressionSyntax member && member.Name == qualified
            || qualified.Parent is QualifiedNameSyntax qualifiedName && qualifiedName.Right == qualified
            || qualified.Parent is AliasQualifiedNameSyntax aliasName && aliasName.Name == qualified)
        {
            qualified = qualified.Parent;
        }

        if (qualified.Parent is MemberAccessExpressionSyntax { Name: SimpleNameSyntax { Identifier.ValueText: MethodName } } access
            && access.Expression == qualified
            && access.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == access)
        {
            return Classify(invocation, out call);
        }

        return new Finding($"non-call reference to {GateType} (method group, typeof, nameof or type use)", Allowed: true);
    }

    private static Finding? Classify(InvocationExpressionSyntax invocation, out (bool Delegate, bool Counted)? call)
    {
        call = null;
        switch (invocation.ArgumentList.Arguments.Count)
        {
            case LimiterOverloadArguments:
                call = (false, true);

                // The limiter is the second parameter; anything but a plain name or member access could hide a fake.
                var limiter = invocation.ArgumentList.Arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == "limiter")
                    ?? invocation.ArgumentList.Arguments[1];

                return limiter.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax or ThisExpressionSyntax, Name: IdentifierNameSyntax }
                    ? null
                    : new Finding("limiter overload called with a limiter argument that is not a plain identifier or member access", Allowed: false);
            case DelegateOverloadArguments:
                call = (true, true);

                return new Finding("delegate overload called; only the limiter overload in the allowed file may call it", Allowed: true);
            default:
                return new Finding($"unresolved call shape: {invocation.ArgumentList.Arguments.Count} arguments match neither overload", Allowed: false);
        }
    }

    private sealed record Finding(string Message, bool Allowed);

    private sealed record ScanReport(List<string> Violations, string[] DelegateCalls, List<string> LimiterCalls, int Scanned, int Mentions);
}

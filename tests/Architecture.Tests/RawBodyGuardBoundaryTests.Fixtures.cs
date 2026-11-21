using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The mutant proofs for <see cref="RawBodyGuardBoundaryTests"/>: each bypass form is red and named, and the shapes
/// the production tree must keep are green, so a green real-tree scan cannot come from a walk that matches nothing.
/// </summary>
public sealed partial class RawBodyGuardBoundaryTests
{
    private const string RouteFile = Modules + "Evidgate.Modules.Catalog/Endpoints/SyntheticRoute.cs";
    private const string SupportFile = Modules + "Evidgate.Modules.Authoring/Endpoints/SyntheticSupport.cs";
    private const string IdentityFile = Modules + "Evidgate.Modules.Identity/Endpoints/SyntheticRoute.cs";

    private static readonly Reader[] RouteReader = [new(RouteFile, "Route", "ReadBodyAsync", Protection.Caller)];

    private static readonly Reader[] SupportReaders =
    [
        new(SupportFile, "Support", "ReadAsync", Protection.Central),
        new(SupportFile, "Support", "ReadBodyAsync", Protection.Forward),
    ];

    [Fact]
    public void Scan_CallerReaderCalledAfterTheGuard_IsGreen()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nRawBodyGuard.RequireResolvedTenant(context.RequestServices);\nawait ReadBodyAsync(context, token);\n}",
            "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_CentralReaderGuardFirst_IsGreen()
    {
        var source = Source("Support",
            "static async Task M(HttpContext context)\n{\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\nvar body = context.Request.Body;\n}");

        Scan([(SupportFile, source)], [Central("M")]).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_GuardAfterTheRead_IsRed()
    {
        var source = Source("Support",
            "static async Task M(HttpContext context)\n{\nvar body = context.Request.Body;\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\n}");

        Scan([(SupportFile, source)], [Central("M")])
            .ShouldContain(finding => finding.Contains("before the shared guard", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_RequestTouchedBeforeTheGuardIsRed()
    {
        var source = Source("Support",
            "static async Task M(HttpContext context)\n{\nvar length = context.Request.ContentLength;\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\n}");

        Scan([(SupportFile, source)], [Central("M")])
            .ShouldContain(finding => finding.Contains("request touched before the guard", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_GuardInADifferentMethodIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nawait ReadBodyAsync(context, token);\n}",
            "static void GuardThis(HttpContext context) => RawBodyGuard.RequireResolvedTenant(context.RequestServices);",
            "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader)
            .ShouldContain(finding => finding.Contains("invoked before the shared guard", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_GuardInsideALambdaIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nAction<HttpContext> guard = c => RawBodyGuard.RequireResolvedTenant(c);\nguard(context);\nawait ReadBodyAsync(context, token);\n}",
            "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader)
            .ShouldContain(finding => finding.Contains("invoked before the shared guard", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UnicodeEscapedGuardCallIsStillSeen()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nR\\u0061wBodyGuard.RequireResolvedTenant(context.RequestServices);\nawait ReadBodyAsync(context, token);\n}",
            "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_UnicodeEscapedReaderNameIsUnlisted()
    {
        var source = Source("Route",
            "static async Task ReadBody\\u0041sync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], [])
            .ShouldContain(finding => finding.Contains("unlisted reader", StringComparison.Ordinal));
    }

    private const string ReadBodyMember =
        "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}";

    private const string GuardCall = "RawBodyGuard.RequireResolvedTenant(context.RequestServices);";

    private static string RouteWith(string body) =>
        Source("Route", "static async Task M(HttpContext context)\n{\n" + body + "\nawait ReadBodyAsync(context, token);\n}", ReadBodyMember);

    [Theory]
    [InlineData("#if false\n" + GuardCall + "\n#endif")]
    [InlineData("#if NET10_0\n" + GuardCall + "\n#endif")]
    [InlineData("#if NET10_0\nvar x = 1;\n#else\n" + GuardCall + "\n#endif")]
    [InlineData("#if NET10_0\n" + GuardCall + "\n#else\nvar x = 1;\n#endif")]
    [InlineData("#if true\nvar x = 1;\n#endif\n" + GuardCall)]
    public void Scan_ConditionalTextAroundTheGuardIsRed(string body)
    {
        Scan([(RouteFile, RouteWith(body))], RouteReader)
            .ShouldContain(finding => finding.Contains("conditionally compiled text", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_DisabledReaderCallIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\n#if NET10_0\nawait ReadBodyAsync(context, token);\n#endif\n}",
            ReadBodyMember);

        Scan([(RouteFile, source)], RouteReader)
            .ShouldContain(finding => finding.Contains("conditionally compiled text", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_CommentedOutGuardIsNotCountedAndNotConditional()
    {
        var errors = Scan([(RouteFile, RouteWith("// #if NET10_0\n// " + GuardCall))], RouteReader);

        errors.ShouldNotContain(finding => finding.Contains("conditionally compiled text", StringComparison.Ordinal));
        errors.ShouldContain(finding => finding.Contains("invoked before the shared guard", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_CleanGuardedFileIsGreen()
    {
        Scan([(RouteFile, RouteWith(GuardCall))], RouteReader).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_UsingStaticGuardIsRed()
    {
        var source = "using static Evidgate.BuildingBlocks.Pipeline.Tenancy.RawBodyGuard;\n"
            + Source("Route", "static void M(HttpContext context) => RequireResolvedTenant(context);");

        Scan([(RouteFile, source)], [])
            .ShouldContain(finding => finding.Contains("using directive", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_GuardMethodGroupAliasIsRed()
    {
        var source = Source("Route", "static void M(HttpContext context)\n{\nvar guard = RawBodyGuard.RequireResolvedTenant;\n}");

        Scan([(RouteFile, source)], [])
            .ShouldContain(finding => finding.Contains("guard referenced without calling", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_NewRouteReadingItsOwnBodyIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nawait context.Request.Body.ReadAsync(new byte[1], CancellationToken.None);\n}");

        Scan([(RouteFile, source)], [])
            .ShouldContain(finding => finding.Contains("raw body access outside an allow-listed reader", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_RequestAliasBodyReaderIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nvar req = context.Request;\nawait req.BodyReader.ReadAsync();\n}");

        Scan([(RouteFile, source)], [])
            .ShouldContain(finding => finding.Contains("raw body access outside an allow-listed reader", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ForwarderToACentralReaderIsGreen()
    {
        var source = Source("Support",
            "static Task ReadBodyAsync(HttpContext context, CancellationToken token) => ReadAsync(context, token);",
            "static async Task ReadAsync(HttpContext context, CancellationToken token)\n{\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\nvar body = context.Request.Body;\n}");

        Scan([(SupportFile, source)], SupportReaders).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ForwarderToAnUnguardedReaderIsRed()
    {
        var source = Source("Support",
            "static Task ReadBodyAsync(HttpContext context, CancellationToken token) => ReadRaw(context, token);",
            "static async Task ReadAsync(HttpContext context, CancellationToken token)\n{\nRawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);\nvar body = context.Request.Body;\n}",
            "static async Task ReadRaw(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(SupportFile, source)], SupportReaders)
            .ShouldContain(finding => finding.Contains("must reach a centrally guarded reader", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_AllowListedFileMissingFromTheTreeIsStale()
    {
        Scan([(SupportFile, Source("Support", "static void M() { }"))], RouteReader)
            .ShouldContain(finding => finding.Contains("stale reader allow-list entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_AllowListedReaderRenamedIsStale()
    {
        var source = Source("Route",
            "static async Task FetchAsync(HttpContext context, CancellationToken token)\n{\nRawBodyGuard.RequireResolvedTenant(context.RequestServices);\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader)
            .ShouldContain(finding => finding.Contains("stale reader allow-list entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UnrelatedStreamReadAsyncIsGreen()
    {
        var source = Source("Route",
            "static async Task M(MemoryStream stream, CancellationToken token)\n{\nawait stream.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], []).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_NonScopedModuleRawReadIsGreen()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nawait context.Request.Body.ReadAsync(new byte[1], CancellationToken.None);\n}");

        Scan([(IdentityFile, source)], []).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_LocalTenantAccessorIsRed()
    {
        var source = Source("Route",
            "static bool M(IServiceProvider services) => services.GetRequiredService<IRequestTenantContextAccessor>().Current is null;");

        Scan([(RouteFile, source)], [])
            .ShouldContain(finding => finding.Contains("local tenant accessor", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SourceThatDoesNotParseIsRed()
    {
        Scan([(RouteFile, "static class Route { void M( { RawBodyGuard.RequireResolvedTenant(")], [])
            .ShouldContain(finding => finding.Contains("does not parse", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_GuardWithoutDirectGuardStatementIsRed()
    {
        var source = Source("Support",
            "static async Task M(HttpContext context)\n{\nGuard(context);\nvar body = context.Request.Body;\n}",
            "static void Guard(HttpContext context) => RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);");

        Scan([(SupportFile, source)], [Central("M")])
            .ShouldContain(finding => finding.Contains("must call RawBodyGuard", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ReaderMethodGroupAliasIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nvar read = ReadBodyAsync;\nawait read(context, token);\n}",
            "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader)
            .ShouldContain(finding => finding.Contains("reader referenced without being called directly", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_CallerReaderReadBeforeTheGuardIsRed()
    {
        var source = Source("Route",
            "static async Task M(HttpContext context)\n{\nawait ReadBodyAsync(context, token);\nRawBodyGuard.RequireResolvedTenant(context.RequestServices);\n}",
            "static async Task ReadBodyAsync(HttpContext context, CancellationToken token)\n{\nawait context.Request.Body.ReadAsync(new byte[1], token);\n}");

        Scan([(RouteFile, source)], RouteReader)
            .ShouldContain(finding => finding.Contains("invoked before the shared guard", StringComparison.Ordinal));
    }

    private static Reader Central(string method) => new(SupportFile, "Support", method, Protection.Central);

    private static string Source(string type, params string[] members) => "using System;\nusing System.Threading;\n"
        + "namespace Synthetic;\n\nstatic class " + type + "\n{\n" + string.Join("\n", members) + "\n}\n";
}

using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Synthetic route maps pin every refusal shape of the Authoring write-route scan, so each shape still fails when
/// the scanner is changed: a removed, late, nested or wrong-argument guard, an alias, a lambda, an expression-bodied
/// delegate, an unresolvable or overloaded method group, an unsupported <c>Map*</c> write call, conditionally compiled
/// text, a write route that arrives without a guard, and a stale or invalid exemption.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private const string Guard = "RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);";

    private static string Handler(string body, string name = "M") =>
        $"private static Task {name}(HttpContext context)\n{{\n{body}\n}}";

    private static string Register(string method = "MapPost", string handler = "M", string path = "/x") =>
        $"private static void Register(RouteGroupBuilder authoring) => authoring.{method}(\"{path}\", {handler});";

    private static (string Path, string Text) Synthetic(params string[] members) =>
        (Endpoints + "SyntheticRoute.cs", "namespace N;\nstatic class Route\n{\n" + string.Join("\n", members) + "\n}\n");

    [Fact]
    public void Scan_GuardFirstOnAPlainMethodGroup_IsGreen()
    {
        var found = new List<string>();

        Scan([Synthetic(Register(), Handler($"{Guard}\nSupport.IfMatchVersion(context, out var v);\nreturn Task.CompletedTask;"))], found)
            .ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_NullCheckThenGuard_IsGreen()
    {
        var found = new List<string>();

        Scan([Synthetic(Register(), Handler($"ArgumentNullException.ThrowIfNull(context);\n{Guard}\nreturn Task.CompletedTask;"))], found)
            .ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_GuardRemoved_IsRed()
    {
        var errors = Scan([Synthetic(Register(), Handler("return Task.CompletedTask;"))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldStartWith(Endpoints + "SyntheticRoute.cs:", Case.Sensitive, "the error names its file and line");
    }

    [Fact]
    public void Scan_GuardAfterAnotherStatement_IsRed()
    {
        Scan([Synthetic(Register(), Handler($"var q = context.Request.Query[\"format\"];\n{Guard}\nreturn Task.CompletedTask;"))], [])
            .Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_WrapperWithAnotherName_IsRed()
    {
        Scan([Synthetic(
            Register(handler: "Wrapper"),
            Handler("return M(context);", "Wrapper"),
            Handler($"{Guard}\nreturn Task.CompletedTask;", "M"))], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_MethodGroupAlias_IsRed()
    {
        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring)\n{\nFunc<HttpContext, Task> h = M;\nauthoring.MapPost(\"/x\", h);\n}",
            Handler($"{Guard}\nreturn Task.CompletedTask;"))], []).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData("Action<RequestDelegate> register = SaveAsync => authoring.MapPut(string.Empty, SaveAsync);", "SaveAsync")]
    [InlineData("Action<RequestDelegate> register = x => authoring.MapPost(\"/x\", x);", "x")]
    [InlineData("Action<RequestDelegate> register = (SaveAsync) => authoring.MapPut(string.Empty, SaveAsync);", "SaveAsync")]
    [InlineData("Action<RequestDelegate> register = delegate(RequestDelegate SaveAsync) { authoring.MapPut(string.Empty, SaveAsync); };", "SaveAsync")]
    [InlineData("void RegisterWrite(RequestDelegate SaveAsync) => authoring.MapPut(string.Empty, SaveAsync);", "SaveAsync")]
    public void Scan_EnclosingFunctionParameterShadowsGuardedMethod_IsRed(string registration, string name)
    {
        var errors = Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring)\n{\n" + registration + "\n}",
            Handler($"{Guard}\nreturn Task.CompletedTask;", name))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("not a lambda, member or alias", Case.Sensitive);
    }

    [Fact]
    public void Scan_LambdaDelegate_IsRed()
    {
        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring) => authoring.MapPost(\"/x\", (HttpContext context) => RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices));")], [])
            .Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_UnresolvedMethodGroup_IsRed()
    {
        Scan([Synthetic(Register(handler: "Missing"))], []).Length.ShouldBe(1);
        Scan([Synthetic(
            Register(),
            Handler($"{Guard}\nreturn Task.CompletedTask;"),
            Handler($"{Guard}\nreturn Task.CompletedTask;", "M").Replace("(HttpContext context)", "(HttpContext context, string extra)", StringComparison.Ordinal))], [])
            .Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_ExpressionBodiedDelegate_IsRed()
    {
        Scan([Synthetic(Register(), $"private static Task M(HttpContext context) => {Guard}")], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_WriteRouteAddedWithoutGuard_IsRed()
    {
        Scan([Synthetic(
            Register(),
            Register(handler: "N", path: "/y"),
            Handler($"{Guard}\nreturn Task.CompletedTask;"),
            Handler("return Task.CompletedTask;", "N"))], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_GuardNested_IsRed()
    {
        Scan([Synthetic(Register(), Handler($"if (context is not null) {{ {Guard} }}\nreturn Task.CompletedTask;"))], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_GuardWithTheWrongArgument_IsRed()
    {
        Scan([Synthetic(Register(), Handler("RawBodyGuard.RequireAuthenticatedTenant(other.RequestServices);\nreturn Task.CompletedTask;"))], [])
            .Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_UnsupportedWriteCapableMapCall_IsRed()
    {
        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring) => authoring.MapMethods(\"/x\", new[] { \"POST\" }, M);",
            Handler($"{Guard}\nreturn Task.CompletedTask;"))], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_WriteCallInsideADisabledRegion_IsRed()
    {
        var errors = Scan([Synthetic("#if NET10_0\n" + Register() + "\n#endif", Handler($"{Guard}\nreturn Task.CompletedTask;"))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("conditionally compiled text in an endpoint file hides code from the pin", Case.Sensitive);
    }

    [Fact]
    public void Scan_PreconditionInsideADisabledRegionBeforeGuard_IsRed()
    {
        var errors = Scan([Synthetic(
            Register(),
            Handler($"#if NET10_0\nSupport.IfMatchVersion(context, out var v);\n#endif\n{Guard}\nreturn Task.CompletedTask;"))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldStartWith(Endpoints + "SyntheticRoute.cs:", Case.Sensitive, "the error names its file and line");
        errors[0].ShouldContain(": conditionally compiled text in an endpoint file hides code from the pin", Case.Sensitive);
    }

    [Fact]
    public void Scan_CommentedRegistrationInsideADisabledRegion_IsRed()
    {
        var errors = Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring)\n{\n#if NET10_0\nauthoring.MapPost /* registration */ (\"/unguarded\", UnsafeAsync);\n#endif\n}",
            Handler("return Task.CompletedTask;", "UnsafeAsync"))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldStartWith(Endpoints + "SyntheticRoute.cs:", Case.Sensitive, "the error names its file and line");
        errors[0].ShouldContain(": conditionally compiled text in an endpoint file hides code from the pin", Case.Sensitive);
    }

    [Theory]
    [InlineData("#if NET10_0\nconst int value = 1;\n#endif")]
    [InlineData("#if true\nconst int value = 1;\n#endif")]
    [InlineData("#if false\n#elif true\nconst int value = 1;\n#endif")]
    public void Scan_ConditionalTextUnrelatedToRegistrations_IsRed(string conditional)
    {
        var errors = Scan([Synthetic(Register(), Handler($"{Guard}\n{conditional}\nreturn Task.CompletedTask;"))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("conditionally compiled text in an endpoint file hides code from the pin", Case.Sensitive);
    }

    [Fact]
    public void Scan_ConditionalAccessWriteCall_IsRed()
    {
        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder? authoring) => authoring?.MapPost(\"/x\", M);",
            Handler("return Task.CompletedTask;"))], []).Length.ShouldBe(1);
        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder? authoring) => authoring?.MapMethods(\"/x\", new[] { \"PUT\" }, M);",
            Handler($"{Guard}\nreturn Task.CompletedTask;"))], []).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_UnrecognizedMapCallShape_IsRed()
    {
        var errors = Scan([Synthetic(
            "private static void Register(RouteGroupBuilder? authoring) => (authoring?.MapPost)(\"/x\", M);",
            Handler($"{Guard}\nreturn Task.CompletedTask;"))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("unrecognized Map call shape", Case.Sensitive);
    }

    [Fact]
    public void Scan_ConditionalAccessReadCall_IsOutsideTheRule()
    {
        var found = new List<string>();

        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder? authoring) => authoring?.MapGet(\"/x\", Get);",
            Handler("return Task.CompletedTask;", "Get"))], found).ShouldBeEmpty();
        found.ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ReadRoutesAndEndpointComposition_AreOutsideTheRule()
    {
        var found = new List<string>();

        Scan([Synthetic(
            Register(method: "MapGet", handler: "Get"),
            "private static void Compose(RouteGroupBuilder authoring) => AuthoringNodeEndpoints.MapAuthoringNodeEndpoints(authoring);",
            Handler("return Task.CompletedTask;", "Get"))], found).ShouldBeEmpty();
        found.ShouldBeEmpty("a read delegate is not a write delegate");
    }

    [Fact]
    public void Scan_DeclaredExemption_SuppressesTheGuardError()
    {
        var found = new List<string>();

        Scan(
            [Synthetic(Register(), Handler("return Task.CompletedTask;"))],
            found,
            [("Route.M", "kept for a route whose guard lands separately")]).ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_ExemptionForAGuardedDelegate_IsStale()
    {
        Scan(
            [Synthetic(Register(), Handler($"{Guard}\nreturn Task.CompletedTask;"))],
            [],
            [("Route.M", "no longer needs the exemption")]).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_ExemptionForANonWriteDelegate_IsStale()
    {
        Scan(
            [Synthetic(Register(method: "MapGet", handler: "Get"), Handler("return Task.CompletedTask;", "Get"))],
            [],
            [("Route.Get", "was a write delegate before it became a read")]).Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_ExemptionWithoutAReason_IsInvalid()
    {
        Scan(
            [Synthetic(Register(), Handler("return Task.CompletedTask;"))],
            [],
            [("Route.M", " ")]).Length.ShouldBe(1);
    }
}

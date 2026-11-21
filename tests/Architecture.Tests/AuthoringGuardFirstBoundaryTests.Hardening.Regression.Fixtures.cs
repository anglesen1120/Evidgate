using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed partial class AuthoringGuardFirstBoundaryTests
{
    [Theory]
    [InlineData("r.Produces(200).RequireAuthorization();")]
    [InlineData("(r).Produces(200).RequireAuthorization();")]
    [InlineData("r!.Produces(200).RequireAuthorization();")]
    [InlineData("((RouteHandlerBuilder)r).Produces(200).RequireAuthorization();")]
    [InlineData("r?.Produces(200).RequireAuthorization();")]
    [InlineData("r.Produces(200)?.RequireAuthorization();")]
    [InlineData("((RouteHandlerBuilder)r!).Produces(200).RequireAuthorization();")]
    public void Scan_StoredBuilderContinuation_IsRed(string continuation)
    {
        Scan([Synthetic(RegistrationBody("var r = authoring.MapPost(\"/x\", M); " + continuation), GuardedHandler())], [])
            .ShouldContain(error => error.Contains("stored route builder", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Route((authoring), \"POST\", M);")]
    [InlineData("Route((IEndpointRouteBuilder)authoring, \"POST\", M);")]
    [InlineData("Route(authoring!, \"POST\", M);")]
    [InlineData("Route(flag ? authoring : other, \"POST\", M);")]
    [InlineData("Route(other ?? authoring, \"POST\", M);")]
    [InlineData("var r = flag ? authoring : other; Route(r, \"POST\", M);")]
    [InlineData("var r = other ?? (IEndpointRouteBuilder)authoring; Route(r, \"POST\", M);")]
    [InlineData("RouteGroupBuilder r; r = authoring; Route(r, \"POST\", M);")]
    [InlineData("RouteHandlerBuilder r; r = authoring.MapPost(\"/x\", M); r.Produces(200).RequireAuthorization();")]
    public void Scan_WrappedOrAssignedBuilder_IsRed(string registration)
    {
        Scan([Synthetic(RegistrationBody(registration), GuardedHandler())], [])
            .ShouldContain(error => error.Contains("non-Map helper", StringComparison.Ordinal)
                || error.Contains("stored route builder", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("authoring?.FilterFactories.Add((c, next) => next);")]
    [InlineData("authoring?.Conventions.Add(builder => { });")]
    [InlineData("((RouteGroupBuilder)authoring!).Conventions.Add(builder => { });")]
    [InlineData("authoring.Nested.Conventions.Add(builder => { });")]
    [InlineData("authoring.MapGroup(\"/sub\").FilterFactories.Add((c, next) => next);")]
    public void Scan_ConditionalOrChainedConventionCollection_IsRed(string convention)
    {
        Scan([Synthetic(RegistrationBody("authoring.MapPost(\"/x\", M); " + convention), GuardedHandler())], [])
            .ShouldContain(error => error.Contains("convention collection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("var count = Settings.Conventions.Count;")]
    [InlineData("var count = Settings?.Conventions.Count;")]
    [InlineData("Settings.FilterFactories.Add(factory);")]
    public void Scan_UnrelatedConventionCollection_IsGreen(string expression)
    {
        Scan([Synthetic(RegistrationBody("authoring.MapPost(\"/x\", M); " + expression), GuardedHandler())], [])
            .ShouldBeEmpty();
    }

    [Fact]
    public void Scan_NestedAndFileScopedNamespacePartialOverload_IsRed()
    {
        var first = PartialRoute("First", Register() + GuardedHandler(), "N.Inner");
        var second = (Endpoints + "Second.cs",
            "namespace N { namespace Inner { static partial class Route { private static Task M(HttpContext context, string body) => Task.CompletedTask; } } }");
        Scan([first], [], universe: [first, second])
            .ShouldContain(error => error.Contains("partial type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("app.MapGroup(\"\").AddEndpointFilter<Evil>().MapAuthoringEndpoints()" + ComposerFilters + ";")]
    [InlineData("var r = app.MapAuthoringEndpoints()" + ComposerFilters + "; var alias = r;")]
    [InlineData("Change(app.MapAuthoringEndpoints()" + ComposerFilters + ");")]
    [InlineData("var r = app.MapAuthoringEndpoints()" + ComposerFilters + "; Change(r);")]
    [InlineData("app.MapAuthoringEndpoints()" + ComposerFilters + "; app.MapAuthoringEndpoints()" + ComposerFilters + ";")]
    [InlineData("app.MapOtherEndpoints();")]
    public void Scan_ComposerReceiverEscapeOrCount_IsRed(string registration)
    {
        var route = Synthetic(Register(), GuardedHandler());
        Scan([route], [], universe: [route, Host(registration)])
            .ShouldContain(error => error.Contains("composer result", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Route.Check();", "private static void Check() { throw new InvalidOperationException(); }")]
    [InlineData("this.Check();", "private void Check() { throw new InvalidOperationException(); }")]
    [InlineData("Check<int>();", "private static void Check<T>() { throw new InvalidOperationException(); }")]
    [InlineData("Route.Check<int>();", "private static void Check<T>() { throw new InvalidOperationException(); }")]
    [InlineData("Check();", "private static void Check() { } private static void Check(int value) { throw new InvalidOperationException(); }")]
    [InlineData("ObjectDisposedException.ThrowIf(disposed, this);", "")]
    [InlineData("System.ArgumentOutOfRangeException.ThrowIfNegative(value);", "")]
    [InlineData("global::System.ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1);", "")]
    [InlineData("void Check() { throw new InvalidOperationException(); } Check();", "")]
    [InlineData("IResult Check() => Results.BadRequest(); Check();", "")]
    [InlineData("Func<int> check = () => throw new InvalidOperationException(); check();", "")]
    [InlineData("Func<IResult> check = () => Results.BadRequest(); check();", "")]
    [InlineData("var check = IResult () => Results.BadRequest(); check();", "")]
    public void Scan_QualifiedOverloadedOrLocalRefusal_IsRed(string refusal, string helper)
    {
        Scan([Synthetic(ReadRegister(), ReadHandler($"{Guard}\n{refusal}\nreturn await mediator.Send(new Q());"), helper)], [], reads: true)
            .ShouldContain(error => error.Contains("helper refusal", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_HelperInAnotherPartialFile_IsRed()
    {
        var first = PartialRoute("First", ReadRegister() + ReadHandler($"{Guard}\nRoute.Check();\nreturn await mediator.Send(new Q());"));
        var second = PartialRoute("Second", "private static void Check() { throw new InvalidOperationException(); }");
        Scan([first], [], universe: [first, second], reads: true)
            .ShouldContain(error => error.Contains("helper refusal", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UninvokedLocalRefusal_IsGreen()
    {
        Scan([Synthetic(ReadRegister(), ReadHandler($"{Guard}\nvoid Check() {{ throw new InvalidOperationException(); }}\nFunc<IResult> refuse = () => Results.BadRequest();\nreturn await mediator.Send(new Q());"))], [], reads: true)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Guid")]
    [InlineData("HttpContext")]
    [InlineData("IMediator")]
    [InlineData("CancellationToken")]
    [InlineData("TenantResolutionFilter")]
    [InlineData("ResultEndpointFilter")]
    public void Scan_ProtectedGlobalAliasOutsideRouteFiles_IsRed(string name)
    {
        var route = Synthetic(Register(), GuardedHandler());
        var other = ("src/Other/GlobalUsings.cs", $"global using {name} = CustomType;");
        Scan([route], [], universe: [route, other])
            .ShouldContain(error => error.Contains("using alias", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_HostFilterAlias_IsRed()
    {
        var route = Synthetic(Register(), GuardedHandler());
        var host = Host("app.MapAuthoringEndpoints()" + ComposerFilters + ";");
        host.Text = "using TenantResolutionFilter = Evil;\n" + host.Text;
        Scan([route], [], universe: [route, host])
            .ShouldContain(error => error.Contains("using alias", StringComparison.Ordinal));
    }
}

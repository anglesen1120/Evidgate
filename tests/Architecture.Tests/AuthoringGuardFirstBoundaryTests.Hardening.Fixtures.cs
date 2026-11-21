using Shouldly;
using Xunit;

namespace Architecture.Tests;

public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private static string RegistrationBody(string body) =>
        "private static void Register(RouteGroupBuilder authoring) { " + body + " }";

    [Theory]
    [InlineData("r.RequireAuthorization();")]
    [InlineData("r.WithMetadata(new Marker());")]
    [InlineData("r.AddEndpointFilter<Filter>();")]
    [InlineData("var alias = r; alias.RequireAuthorization();")]
    [InlineData("Consume(authoring.MapPost(\"/x\", M).Produces(200));")]
    public void Scan_ConventionOnStoredBuilder_IsRed(string convention)
    {
        var errors = Scan([Synthetic(
            RegistrationBody("var r = authoring.MapPost(\"/x\", M); " + convention), GuardedHandler())], []);

        errors.ShouldContain(error => error.Contains("stored route builder", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_AllowedConventionOnStoredBuilder_IsGreen()
    {
        Scan([Synthetic(RegistrationBody("var r = authoring.MapPost(\"/x\", M); r.Produces(200);"), GuardedHandler())], [])
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("authoring.FilterFactories.Add((c, next) => next);")]
    [InlineData("authoring.Conventions.Add(builder => { });")]
    [InlineData("var factories = authoring.FilterFactories;")]
    public void Scan_GroupConventionCollection_IsRed(string convention)
    {
        var errors = Scan([Synthetic(RegistrationBody("authoring.MapPost(\"/x\", M); " + convention), GuardedHandler())], []);

        errors.ShouldContain(error => error.Contains("convention collection", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UnrelatedCollectionAdd_IsGreen()
    {
        Scan([Synthetic(RegistrationBody("authoring.MapPost(\"/x\", M); values.Add(1);"), GuardedHandler())], [])
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("authoring.Post(\"/x\", M);")]
    [InlineData("Route(authoring, \"POST\", M);")]
    [InlineData("Route(authoring, \"POST\", context => Results.Ok());")]
    [InlineData("var alias = authoring; alias.Post(\"/x\", M);")]
    [InlineData("var r = authoring.MapPost(\"/x\", M); Change(r);")]
    public void Scan_NonMapRegistrationHelper_IsRed(string registration)
    {
        var errors = Scan([Synthetic(RegistrationBody(registration), GuardedHandler())], []);

        errors.ShouldContain(error => error.Contains("non-Map helper", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_MapRegistrationAndUnrelatedHelper_IsGreen()
    {
        Scan([Synthetic(RegistrationBody("authoring.MapPost(\"/x\", M); Route(values, \"POST\", M);"), GuardedHandler())], [])
            .ShouldBeEmpty();
    }

    private static (string Path, string Text) PartialRoute(string name, string members, string ns = "N") =>
        (Endpoints + name + ".cs", "namespace " + ns + "; static partial class Route { " + members + " }");

    [Fact]
    public void Scan_HandlerOverloadInAnotherPartial_IsRed()
    {
        var first = PartialRoute("First", Register() + GuardedHandler());
        var second = PartialRoute("Second", "private static Task M(HttpContext context, string body) => Task.CompletedTask;");

        Scan([first], [], universe: [first, second])
            .ShouldContain(error => error.Contains("partial type", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SameHandlerNameInDifferentNamespace_IsGreen()
    {
        var first = PartialRoute("First", Register() + GuardedHandler());
        var second = PartialRoute("Second", "private static Task M(HttpContext context, string body) => Task.CompletedTask;", "Other");

        Scan([first], [], universe: [first, second]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("using X = System.Guid;")]
    [InlineData("using Guid = CustomBody;")]
    [InlineData("using HttpContext = CustomBody;")]
    public void Scan_UsingAliasInEndpointFile_IsRed(string alias)
    {
        var file = Synthetic(Register(), GuardedHandler());
        file.Text = alias + "\n" + file.Text;

        Scan([file], []).ShouldContain(error => error.Contains("using alias", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_OrdinaryUsingInEndpointFile_IsGreen()
    {
        var file = Synthetic(Register(), GuardedHandler());
        file.Text = "using System;\n" + file.Text;

        Scan([file], []).ShouldBeEmpty();
    }

    private const string ComposerFilters =
        ".AddEndpointFilter<TenantResolutionFilter>().AddEndpointFilter<ResultEndpointFilter>()";

    private static (string Path, string Text) Host(string registration) =>
        ("src/Evidgate.Api/ApiHost.cs", "class ApiHost { void Register(WebApplication app) { " + registration + " } }");

    [Theory]
    [InlineData("app.MapAuthoringEndpoints()" + ComposerFilters + ".AddEndpointFilter<Extra>();")]
    [InlineData("app.MapAuthoringEndpoints().AddEndpointFilter<ResultEndpointFilter>().AddEndpointFilter<TenantResolutionFilter>();")]
    [InlineData("app.MapAuthoringEndpoints()" + ComposerFilters + ".WithMetadata(new Marker());")]
    [InlineData("var r = app.MapAuthoringEndpoints()" + ComposerFilters + "; r.WithMetadata(new Marker());")]
    [InlineData("app.MapAuthoringEndpoints().AddEndpointFilter<TenantResolutionFilter>();")]
    public void Scan_ComposerResultExtraOrChangedFilter_IsRed(string registration)
    {
        var route = Synthetic(Register(), GuardedHandler());
        var host = Host(registration);

        Scan([route], [], null, [route, host])
            .ShouldContain(error => error.Contains("composer result", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ComposerResultExactFilters_IsGreen()
    {
        var route = Synthetic(Register(), GuardedHandler());
        var host = Host("app.MapAuthoringEndpoints()" + ComposerFilters + ";");

        Scan([route], [], null, [route, host]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("var refusal = Refuse();", "private static IResult Refuse() => Results.BadRequest();")]
    [InlineData("Check();", "private static void Check() { throw new InvalidOperationException(); }")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1);", "")]
    [InlineData("Guard.Valid(value);", "")]
    [InlineData("ThrowIfInvalid(value);", "")]
    [InlineData("if (Refuse() is null) { return await mediator.Send(new Q()); }", "private static IResult Refuse() => Results.BadRequest();")]
    [InlineData("return await mediator.Send(new Q(Check()));", "private static int Check() => throw new InvalidOperationException();")]
    public void Scan_ReadHelperRefusalBeforeSend_IsRed(string refusal, string helper)
    {
        var tail = refusal.Contains("mediator.Send", StringComparison.Ordinal) ? string.Empty : "return await mediator.Send(new Q());";
        var errors = Scan([Synthetic(ReadRegister(), ReadHandler($"{Guard}\n{refusal}\n{tail}"), helper)], [], reads: true);

        errors.ShouldContain(error => error.Contains("helper refusal", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ReadQueryHelperAndRefusalAfterSend_IsGreen()
    {
        Scan([Synthetic(
            ReadRegister(),
            ReadHandler($"ArgumentNullException.ThrowIfNull(context);\n{Guard}\nvar q = Query();\nvar result = await mediator.Send(q);\nRefuse();\nreturn result;"),
            "private static Q Query() => new Q();",
            "private static IResult Refuse() => Results.BadRequest();")], [], reads: true).ShouldBeEmpty();
    }
}

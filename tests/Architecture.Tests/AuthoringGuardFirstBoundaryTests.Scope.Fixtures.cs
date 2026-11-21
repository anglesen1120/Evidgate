using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Synthetic sources pin the widened scope of the Authoring pin: a write route or composer outside the Endpoints
/// folder, a delegate parameter that can bind request data, and a filter or metadata chain on the route tree.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private const string OtherModule = "src/Modules/Evidgate.Modules.Other/Endpoints/OtherRoute.cs";
    private const string AuthoringRoute = "private static void Register(RouteGroupBuilder routes) => routes.MapGroup(\"/v1/projects/{p}/authoring\").MapPost(\"/x\", M);";

    private static (string Path, string Text) Other(params string[] members) =>
        (OtherModule, "namespace N;\nstatic class Other\n{\n" + string.Join("\n", members) + "\n}\n");

    private static string GuardedHandler() => Handler($"{Guard}\nreturn Task.CompletedTask;");

    private static string[] ScanRoot((string Path, string Text)[] universe, List<string>? found = null) =>
        Scan(AuthoringRouteFiles(universe), found ?? [], null, universe);

    [Fact]
    public void Scan_WriteRouteInAnotherFolderUnderTheAuthoringPath_IsSelectedAndRefused()
    {
        var universe = new[] { Other(AuthoringRoute, Handler("return Task.CompletedTask;")) };

        AuthoringRouteFiles(universe).Select(file => file.Path).ShouldBe([OtherModule]);
        var errors = ScanRoot(universe);

        errors.Length.ShouldBe(1);
        errors[0].ShouldStartWith(OtherModule + ":", Case.Sensitive, "the error names the out-of-folder file");
        errors[0].ShouldContain("must have RawBodyGuard.RequireAuthenticatedTenant", Case.Sensitive);
    }

    [Fact]
    public void Scan_GuardedWriteRouteInAnotherFolderUnderTheAuthoringPath_IsFoundNotRefused()
    {
        var found = new List<string>();

        ScanRoot([Other(AuthoringRoute, GuardedHandler())], found).ShouldBeEmpty();
        found.ShouldBe(["Other.M"]);
    }

    [Fact]
    public void Scan_RouteOutsideTheFolderAndTheAuthoringPath_IsNotSelected()
    {
        var universe = new[] { Other("private static void Register(RouteGroupBuilder routes) => routes.MapPost(\"/other\", M);", Handler("return Task.CompletedTask;")) };

        AuthoringRouteFiles(universe).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ComposerPassedTheAuthoringGroupFromAnotherFolder_IsSelected()
    {
        var universe = new[]
        {
            Other(
                "private static void Compose(RouteGroupBuilder authoring) => Elsewhere.MapElsewhereEndpoints(authoring);"),
        };

        AuthoringRouteFiles(universe).Select(file => file.Path).ShouldBe([OtherModule]);
    }

    [Fact]
    public void Scan_ComposerDeclaredOutsideTheFolder_IsFollowedAndItsUnguardedWriteRefused()
    {
        var composer = Synthetic("private static void Compose(RouteGroupBuilder authoring) => Other.MapOtherEndpoints(authoring);");
        var declared = Other(
            "internal static void MapOtherEndpoints(RouteGroupBuilder group) => group.MapPost(\"/y\", M);",
            Handler("return Task.CompletedTask;"));

        var errors = Scan([composer], [], null, [composer, declared]);

        errors.Length.ShouldBe(1);
        errors[0].ShouldStartWith(OtherModule + ":", Case.Sensitive, "the followed file is scanned, not skipped");
        errors[0].ShouldContain("must have RawBodyGuard.RequireAuthenticatedTenant", Case.Sensitive);
    }

    [Fact]
    public void Scan_ComposerDeclaredOutsideTheFolderWithAGuard_IsFound()
    {
        var composer = Synthetic("private static void Compose(RouteGroupBuilder authoring) => Other.MapOtherEndpoints(authoring);");
        var declared = Other(
            "internal static void MapOtherEndpoints(RouteGroupBuilder group) => group.MapPost(\"/y\", M);",
            GuardedHandler());
        var found = new List<string>();

        Scan([composer], found, null, [composer, declared]).ShouldBeEmpty();
        found.ShouldBe(["Other.M"]);
    }

    [Fact]
    public void Scan_ComposerWithoutADeclaration_IsRefused()
    {
        var composer = Synthetic("private static void Compose(RouteGroupBuilder authoring) => Missing.MapMissingEndpoints(authoring);");

        var errors = Scan([composer], [], null, [composer]);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("composer MapMissingEndpoints has no declaration", Case.Sensitive);
    }

    [Theory]
    [InlineData("[FromBody] CreateRequest request")]
    [InlineData("CreateRequest request")]
    [InlineData("[AsParameters] Query query")]
    [InlineData("string format")]
    [InlineData("Guid? id")]
    [InlineData("System.Guid id")]
    [InlineData("[FromServices] IMediator other")]
    public void Scan_ParameterThatCanBindRequestData_IsRed(string parameter)
    {
        var errors = Scan([Synthetic(Register(), Handler($"{Guard}\nreturn Task.CompletedTask;").Replace("(HttpContext context)", $"(HttpContext context, {parameter})", StringComparison.Ordinal))], []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("can bind request data before the guard", Case.Sensitive);
    }

    [Fact]
    public void Scan_RouteIdsHttpContextMediatorAndToken_AreGreen()
    {
        var handler = Handler($"{Guard}\nreturn Task.CompletedTask;")
            .Replace("(HttpContext context)", "(Guid p, Guid n, HttpContext context, IMediator mediator, CancellationToken cancellationToken)", StringComparison.Ordinal);

        Scan([Synthetic(Register(), handler)], []).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("authoring.MapPost(\"/x\", M).AddEndpointFilter<Filter>();")]
    [InlineData("authoring.MapPost(\"/x\", M).AddEndpointFilterFactory((c, next) => next);")]
    [InlineData("authoring.MapPost(\"/x\", M).Produces(200).AddEndpointFilter(new Filter());")]
    [InlineData("authoring.MapPost(\"/x\", M).WithMetadata(new Marker());")]
    [InlineData("authoring.MapPost(\"/x\", M).WithMetadata(new AcceptsMetadata([\"a\"], \"b\"), new Marker());")]
    [InlineData("authoring.MapPost(\"/x\", M).Add(builder => builder.Metadata.Add(new Marker()));")]
    [InlineData("authoring.MapPost(\"/x\", M).Finally(builder => builder.Metadata.Add(new Marker()));")]
    [InlineData("authoring.MapPost(\"/x\", M); authoring.AddEndpointFilter<Filter>();")]
    [InlineData("var tree = authoring.MapGroup(\"/more\").WithMetadata(new Marker()); tree.MapPost(\"/x\", M);")]
    [InlineData("var tree = authoring.MapGroup(\"/more\").AddEndpointFilter<Filter>(); tree.MapPost(\"/x\", M);")]
    public void Scan_FilterOrMetadataChainOnTheRouteTree_IsRed(string registration)
    {
        var errors = Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring)\n{\n" + registration + "\n}",
            GuardedHandler())], []);

        errors.Length.ShouldBe(1);
    }

    [Fact]
    public void Scan_DeclaredRouteConventionsAndAcceptsMetadata_AreGreen()
    {
        Scan([Synthetic(
            "private static void Register(RouteGroupBuilder authoring) => authoring.MapPost(\"/x\", M)\n"
            + ".Accepts<Body>(\"application/json\").Produces<Reply>(201).Produces(200).WithSummary(\"s\")\n"
            + ".WithBodyLimit(10, handlerRefusesFirst: true).WithMetadata(new AcceptsMetadata([\"a\"], \"b\"));",
            GuardedHandler())], []).ShouldBeEmpty();
    }
}

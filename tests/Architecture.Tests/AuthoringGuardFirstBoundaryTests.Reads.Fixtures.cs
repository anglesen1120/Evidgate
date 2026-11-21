using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Synthetic read routes pin every refusal shape of the Authoring read scan: validation before the guard, a
/// refusal returned before the mediator send, a removed guard, a lambda registration, a send-less delegate, an
/// undeclared missing-context delegate, a stale exemption, and a route-bound number that is not in its template.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private static string ReadHandler(string body, string name = "M") =>
        $"private static async Task {name}(HttpContext context, IMediator mediator)\n{{\n{body}\n}}";

    private static string ReadRegister(string path = "/x", string handler = "M") =>
        Register(method: "MapGet", handler: handler, path: path);

    [Fact]
    public void Scan_ReadValidatesBeforeTheGuard_IsRed()
    {
        var errors = Scan(
            [Synthetic(
                ReadRegister(),
                ReadHandler($"var format = context.Request.Query[\"format\"].ToString();\n{Guard}\nreturn await mediator.Send(new Q(format));"))],
            [],
            reads: true);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("must have RawBodyGuard.RequireAuthenticatedTenant", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadReturnsBeforeMediatorSend_IsRed()
    {
        var errors = Scan(
            [Synthetic(
                ReadRegister(),
                ReadHandler($"{Guard}\nif (AuthoringEndpointSupport.LimitError(context.Request.Query[\"limit\"], out var limit) is {{ }} error)\n{{\nreturn Result.Failure<Q>(error);\n}}\nreturn await mediator.Send(new Q(limit));"))],
            [],
            reads: true);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("returns before mediator.Send", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadGuardRemoved_IsRed()
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), ReadHandler("return await mediator.Send(new Q());"))],
            [],
            reads: true);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("must have RawBodyGuard.RequireAuthenticatedTenant", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadGuardThenQueryThenSend_IsGreen()
    {
        var found = new List<string>();

        Scan(
            [Synthetic(
                ReadRegister(),
                ReadHandler($"ArgumentNullException.ThrowIfNull(context);\n{Guard}\nreturn await mediator.Send(new Q(context.Request.Query[\"cursor\"].ToString()));"))],
            found,
            reads: true).ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_ReadReturnThatCarriesTheSend_IsGreen()
    {
        var found = new List<string>();

        Scan(
            [Synthetic(
                ReadRegister(),
                ReadHandler($"{Guard}\nreturn mediator.Send(new Q()).AsTask();"))],
            found,
            reads: true).ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_ReadReturnInsideALambdaBeforeTheSend_IsGreen()
    {
        Scan(
            [Synthetic(
                ReadRegister(),
                ReadHandler($"{Guard}\nFunc<int> pick = () => {{ return 1; }};\nreturn await mediator.Send(new Q(pick()));"))],
            [],
            reads: true).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ReadWithoutAnyMediatorSend_IsRed()
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), ReadHandler($"{Guard}\nreturn Task.CompletedTask;"))],
            [],
            reads: true);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("must send its query through mediator.Send", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadLambdaRegistration_IsRed()
    {
        var errors = Scan(
            [Synthetic(
                "private static void Register(RouteGroupBuilder authoring) => authoring.MapGet(\"/x\", (HttpContext context, IMediator mediator) => mediator.Send(new Q()));")],
            [],
            reads: true);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("read handler must be a plain same-type method group", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadWithoutHttpContextAndWithoutExemption_IsRed()
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), "private static Task M(IMediator mediator) => mediator.Send(new Q()).AsTask();")],
            [],
            reads: true,
            readExemptions: []);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("must have RawBodyGuard.RequireAuthenticatedTenant", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadWithoutHttpContextWithADeclaredExemption_IsGreen()
    {
        var found = new List<string>();

        Scan(
            [Synthetic(ReadRegister(), "private static Task M(IMediator mediator) => mediator.Send(new Q()).AsTask();")],
            found,
            reads: true,
            readExemptions: [("Route.M", "the template list reads no request data")]).ShouldBeEmpty();
        found.ShouldBe(["Route.M"]);
    }

    [Fact]
    public void Scan_ReadExemptExpressionBodyWithoutASend_IsRed()
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), "private static Task M(IMediator mediator) => Task.CompletedTask;")],
            [],
            reads: true,
            readExemptions: [("Route.M", "the template list reads no request data")]);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("must send its query through mediator.Send", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadExemptionOnAGuardedDelegate_IsRed()
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), ReadHandler($"{Guard}\nreturn await mediator.Send(new Q());"))],
            [],
            reads: true,
            readExemptions: [("Route.M", "kept although the guard landed")]);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("stale or invalid read exemption Route.M", Case.Sensitive);
    }

    [Fact]
    public void Scan_ReadExemptionOnADelegateThatTakesHttpContext_IsRed()
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), ReadHandler("return await mediator.Send(new Q());"))],
            [],
            reads: true,
            readExemptions: [("Route.M", "excused before the delegate took a context")]);

        errors.Length.ShouldBe(1);
        errors[0].ShouldContain("stale or invalid read exemption Route.M", Case.Sensitive);
    }

    [Theory]
    [InlineData("/versions/{version_no:int}", 0)]
    [InlineData("/versions", 1)]
    public void Scan_ReadRouteBoundNumber_NeedItsIntTemplate(string path, int expected)
    {
        var errors = Scan(
            [Synthetic(
                ReadRegister(path: path),
                ReadHandler($"{Guard}\nreturn await mediator.Send(new Q(version_no));")
                    .Replace("(HttpContext context, IMediator mediator)", "(HttpContext context, IMediator mediator, int version_no)", StringComparison.Ordinal))],
            [],
            reads: true);

        errors.Length.ShouldBe(expected);
    }
}

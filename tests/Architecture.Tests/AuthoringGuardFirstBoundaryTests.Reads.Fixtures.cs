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

    [Theory]
    [InlineData("Result.Failure<Q>(error)", 1)]
    [InlineData("await mediator.Send(new Q())", 0)]
    public void Scan_ReadIfElseContainingTheFirstSend_NeedsSendInEveryReturn(string firstReturn, int expected)
    {
        var errors = Scan(
            [Synthetic(
                ReadRegister(),
                ReadHandler($"{Guard}\nif (invalid) {{ return {firstReturn}; }} else {{ return await mediator.Send(new Q()); }}"))],
            [],
            reads: true);

        errors.Length.ShouldBe(expected);
        if (expected != 0)
        {
            errors[0].ShouldContain("returns without mediator.Send in its own branch", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("throw new InvalidOperationException();\nreturn await mediator.Send(new Q());", 1)]
    [InlineData("var value = invalid ? 1 : throw new InvalidOperationException();\nreturn await mediator.Send(new Q(value));", 1)]
    [InlineData("if (invalid) { throw new InvalidOperationException(); } else { return await mediator.Send(new Q()); }", 1)]
    [InlineData("return await mediator.Send(new Q(invalid ? 1 : throw new InvalidOperationException()));", 1)]
    [InlineData("var value = 1;\nreturn await mediator.Send(new Q(value));", 0)]
    [InlineData("var result = await mediator.Send(new Q());\nthrow new InvalidOperationException();", 0)]
    [InlineData("Func<int> pick = () => throw new InvalidOperationException();\nreturn await mediator.Send(new Q());", 0)]
    [InlineData("int Pick() { throw new InvalidOperationException(); }\nreturn await mediator.Send(new Q());", 0)]
    public void Scan_ReadThrowThroughTheFirstSend_IsRefused(string body, int expected)
    {
        var errors = Scan([Synthetic(ReadRegister(), ReadHandler($"{Guard}\n{body}"))], [], reads: true);

        errors.Length.ShouldBe(expected);
        if (expected != 0)
        {
            errors[0].ShouldContain("throws before mediator.Send", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("invalid ? Result.Failure<Q>(error) : await mediator.Send(new Q())", 1)]
    [InlineData("invalid ? await mediator.Send(new Q()) : Result.Failure<Q>(error)", 1)]
    [InlineData("Result.Failure<Q>(error) ?? await mediator.Send(new Q())", 1)]
    [InlineData("await mediator.Send(new Q()) ?? Result.Failure<Q>(error)", 1)]
    [InlineData("await (invalid ? Task.FromResult(Result.Failure<Q>(error)) : mediator.Send(new Q()).AsTask())", 1)]
    [InlineData("await (invalid ? mediator.Send(new Q()).AsTask() : Task.FromResult(Result.Failure<Q>(error)))", 1)]
    [InlineData("await (invalid ? mediator.Send(new Q()) : Task.FromResult(Result.Failure<Q>(error))).ConfigureAwait(false)", 1)]
    [InlineData("invalid ? await mediator.Send(new Q()) : await mediator.Send(new Q())", 0)]
    [InlineData("await mediator.Send(new Q()) ?? await mediator.Send(new Q())", 0)]
    [InlineData("await (invalid ? mediator.Send(new Q()).AsTask() : mediator.Send(new Q()).AsTask())", 0)]
    [InlineData("await (invalid ? mediator.Send(new Q()) : mediator.Send(new Q())).ConfigureAwait(false)", 0)]
    public void Scan_ReadConditionalReturn_NeedsSendInEveryBranch(string expression, int expected)
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), ReadHandler($"{Guard}\nreturn ({expression});"))],
            [],
            reads: true);

        errors.Length.ShouldBe(expected);
        if (expected != 0)
        {
            errors[0].ShouldContain("returns without mediator.Send in its own branch", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("Results.Json(invalid ? problem : await mediator.Send(new Q()))", 1)]
    [InlineData("invalid switch { true => Results.BadRequest(), false => await mediator.Send(new Q()) }", 1)]
    [InlineData("!valid || (await mediator.Send(new Q())).IsFailure", 1)]
    [InlineData("(invalid ? ValueTask.FromResult(refusal) : mediator.Send(new Q())).AsTask()", 1)]
    [InlineData("(invalid ? Task.FromResult(refusal) : mediator.Send(new Q())).WaitAsync(cancellationToken)", 1)]
    [InlineData("mediator.Send(new Q()).AsTask()", 0)]
    [InlineData("await mediator.Send(new Q()).ConfigureAwait(false)", 0)]
    public void Scan_ReadWrappedSendOrBranchingValue_IsRefusedUnproven(string expression, int expected)
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), ReadHandler($"{Guard}\nreturn ({expression});"))],
            [],
            reads: true);

        errors.Length.ShouldBe(expected);
        if (expected != 0)
        {
            errors[0].ShouldContain("returns without mediator.Send in its own branch", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("mediator.Send(new Q(id ?? throw new InvalidOperationException())).AsTask()", 1)]
    [InlineData("mediator.Send(new Q()).AsTask()", 0)]
    public void Scan_ReadExpressionBodyThrow_IsRefused(string expression, int expected)
    {
        var errors = Scan(
            [Synthetic(ReadRegister(), $"private static Task M(IMediator mediator) => {expression};")],
            [],
            reads: true,
            readExemptions: [("Route.M", "the template list reads no request data")]);

        errors.Length.ShouldBe(expected);
        if (expected != 0)
        {
            errors[0].ShouldContain("throws before mediator.Send", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("return await mediator.Send(new Q()).ConfigureAwait(false);")]
    [InlineData("var result = await mediator.Send(new Q());\nreturn Result.Failure<Q>(error);")]
    public void Scan_ReadDirectSendOrReturnAfterTheSend_IsGreen(string body)
    {
        Scan([Synthetic(ReadRegister(), ReadHandler($"{Guard}\n{body}"))], [], reads: true).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MapHead", false, 1)]
    [InlineData("MapHead", true, 0)]
    [InlineData("MapOptions", false, 1)]
    [InlineData("MapOptions", true, 0)]
    public void Scan_ReadHeadAndOptions_NeedTheGuard(string map, bool guarded, int expected)
    {
        var found = new List<string>();
        var errors = Scan(
            [Synthetic(
                Register(method: map),
                ReadHandler($"{(guarded ? Guard : string.Empty)}\nreturn await mediator.Send(new Q());"))],
            found,
            reads: true);

        found.ShouldBe(["Route.M"]);
        errors.Length.ShouldBe(expected);
        if (expected != 0)
        {
            errors[0].ShouldContain("must have RawBodyGuard.RequireAuthenticatedTenant", Case.Sensitive);
        }
    }
}

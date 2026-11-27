using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Every Authoring read route (<c>MapGet</c>, <c>MapHead</c>, <c>MapOptions</c>) starts with the tenant guard and
/// reaches its first <c>mediator.Send</c> before any return. A delegate that refuses its own query would answer
/// 400/422 to an unauthorised caller; only the pipeline's authorisation behaviour may produce the 401/403.
/// </summary>
public sealed partial class AuthoringGuardFirstBoundaryTests
{
    private static readonly (string TypeMethod, string Reason)[] ReadGuardExemptions =
    [
        ("AiSettingsEndpoints.ListTemplatesAsync", "the template list takes no HttpContext and reads no request data; its expression body is the send itself"),
    ];

    private static readonly string[] ExpectedReads =
    [
        "AiSettingsEndpoints.GetAsync",
        "AiSettingsEndpoints.GetVersionAsync",
        "AiSettingsEndpoints.ListTemplatesAsync",
        "AiSettingsEndpoints.ListVersionsAsync",
        "AuthoringChangeEndpoints.GetAsync",
        "AuthoringChangeEndpoints.ListAsync",
        "AuthoringChecklistEndpoints.ListAsync",
        "AuthoringImportEndpoints.ExportAsync",
        "AuthoringIntakeEndpoints.GetAsync",
        "AuthoringManualCaseEndpoints.GetAsync",
        "AuthoringManualCaseEndpoints.ListAsync",
        "AuthoringNodeEndpoints.GetTreeAsync",
        "AuthoringRequirementEndpoints.GetAsync",
        "AuthoringRequirementEndpoints.GetCoverageAsync",
        "AuthoringRequirementEndpoints.ListAsync",
        "AuthoringRequirementEndpoints.ListVersionsAsync",
        "AuthoringTestReportEndpoints.GetAsync",
        "AuthoringTestReportEndpoints.JobAsync",
        "AuthoringTestReportEndpoints.ListAsync",
        "AuthoringVersionEndpoints.GetAsync",
        "AuthoringTrackerEndpoints.CallbackAsync",
        "AuthoringTrackerEndpoints.ListAsync",
        "AuthoringVersionEndpoints.ListAsync",
    ];

    // The OAuth callback derives its tenant from the signed state and runs its consent steps outside one mediator
    // transaction, so it takes its service from the request scope and cannot send one message. It still must open
    // with the tenant guard; every other rule here is waived for these names only.
    private static readonly string[] StateTenantCallbacks = ["AuthoringTrackerEndpoints.CallbackAsync"];

    [Fact]
    public void AuthoringReadDelegates_AllRegisteredReads_GuardFirstAndSendFirst()
    {
        var universe = SolutionLayout.ProductionSources().ToArray();
        var files = AuthoringRouteFiles(universe);

        files.Count(file => file.Path.StartsWith(Endpoints, StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(8, "the scan must read the Authoring endpoint files");

        var found = new List<string>();
        Scan(files, found, null, universe, reads: true).ShouldBeEmpty();
        found.Order(StringComparer.Ordinal).ShouldBe(ExpectedReads.Order(StringComparer.Ordinal));
        foreach (var (name, _) in ReadGuardExemptions)
        {
            found.ShouldContain(name, "a read exemption must keep naming a registered delegate");
        }
    }

    private void ScanRead(
        InvocationExpressionSyntax call,
        string path,
        List<string> found,
        List<string> errors,
        (string TypeMethod, string Reason)[] exemptions)
    {
        if (ResolveHandler(call, path, "read", errors) is not { } resolved)
        {
            return;
        }

        var (method, name) = resolved;
        found.Add(name);
        var route = (call.ArgumentList.Arguments.ElementAtOrDefault(0)?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
        var guarded = GuardFirst(method);
        if (StateTenantCallbacks.Contains(name, StringComparer.Ordinal))
        {
            if (!guarded)
            {
                errors.Add($"{path}:{Line(method)}: {name} must have RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices) first");
            }

            return;
        }

        errors.AddRange(ParameterErrors(method, path, name, route));
        var hasContext = method.ParameterList.Parameters.Any(parameter =>
            parameter.Type?.ToString().EndsWith("HttpContext", StringComparison.Ordinal) == true);
        var exemption = exemptions.Where(candidate => candidate.TypeMethod == name).ToArray();
        if (exemption.Length != 0)
        {
            // An exemption only stands for a delegate that cannot read the request at all; once it takes an
            // HttpContext the guard is enforceable, so keeping the exemption would fail open.
            if (guarded || hasContext || exemption.Length != 1 || string.IsNullOrWhiteSpace(exemption[0].Reason))
            {
                errors.Add($"{path}:{Line(method)}: stale or invalid read exemption {name}");
            }
        }
        else if (!guarded)
        {
            errors.Add($"{path}:{Line(method)}: {name} must have RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices) first");
        }

        errors.AddRange(EarlyReturnErrors(method, path, name));
    }

    // A read delegate may shape its query after the guard, but it must not answer a validation failure itself:
    // the mediator send is what runs the authorisation behaviour, so a return before it hides the caller's 403.
    // The statement that carries the send is no exception: a return beside it is refused unless its own
    // expression reaches a send, a conditional or coalescing expression counts only when every branch does, and
    // a throw before or inside that statement bypasses the pipeline's authorisation before it can answer.
    private List<string> EarlyReturnErrors(MethodDeclarationSyntax method, string path, string name)
    {
        var errors = new List<string>();
        var mediator = method.ParameterList.Parameters
            .FirstOrDefault(parameter => parameter.Type?.ToString().EndsWith("IMediator", StringComparison.Ordinal) == true)
            ?.Identifier.ValueText;
        errors.AddRange(HelperRefusalErrors(method, mediator, path, name));
        if (method.Body is not { } body)
        {
            var expression = method.ExpressionBody?.Expression;
            if (expression is null || !YieldsSend(expression, mediator))
            {
                errors.Add($"{path}:{Line(method)}: {name} must send its query through mediator.Send so authorisation runs");
            }
            else
            {
                errors.AddRange(ThrowErrors(expression, path, name));
            }

            return errors;
        }

        var send = body.Statements.FirstOrDefault(statement => ContainsSend(statement, mediator));
        if (send is null)
        {
            errors.Add($"{path}:{Line(method)}: {name} must send its query through mediator.Send so authorisation runs");
            return errors;
        }

        foreach (var statement in body.Statements.TakeWhile(statement => statement != send))
        {
            errors.AddRange(ThrowErrors(statement, path, name));
            foreach (var returnStatement in OwnedReturns(statement))
            {
                errors.Add($"{path}:{Line(returnStatement)}: {name} returns before mediator.Send, so a refusal would answer before authorisation");
            }
        }

        errors.AddRange(ThrowErrors(send, path, name));
        foreach (var returnStatement in OwnedReturns(send).Where(statement => !YieldsSend(statement.Expression, mediator)))
        {
            errors.Add($"{path}:{Line(returnStatement)}: {name} returns without mediator.Send in its own branch, so a refusal would answer before authorisation");
        }

        return errors;
    }

    // Fail closed: await, casts, parentheses and the ConfigureAwait/AsTask wrappers are unwrapped, and a
    // conditional or coalescing value is allowed only when every branch reaches a send. Any other wrapper
    // (Results.Json, a switch expression, && / ||, WaitAsync, ...) is unproven and refused, so a refusal branch
    // can never ride beside the send inside one value; a send nested in an argument never counts on its own.
    private static bool YieldsSend(ExpressionSyntax? expression, string? mediator) => expression switch
    {
        null => false,
        AwaitExpressionSyntax awaitExpression => YieldsSend(awaitExpression.Expression, mediator),
        ParenthesizedExpressionSyntax parenthesized => YieldsSend(parenthesized.Expression, mediator),
        CastExpressionSyntax cast => YieldsSend(cast.Expression, mediator),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: { } receiver } } wrapper
            when CallName(wrapper) is "ConfigureAwait" or "AsTask" => YieldsSend(receiver, mediator),
        ConditionalExpressionSyntax conditional =>
            YieldsSend(conditional.WhenTrue, mediator) && YieldsSend(conditional.WhenFalse, mediator),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression) =>
            YieldsSend(binary.Left, mediator) && YieldsSend(binary.Right, mediator),
        InvocationExpressionSyntax call => IsSend(call, mediator),
        _ => false,
    };

    private static IEnumerable<string> ThrowErrors(SyntaxNode node, string path, string name) =>
        OwnedNodes(node)
            .Where(child => child is ThrowStatementSyntax or ThrowExpressionSyntax)
            .Select(child => $"{path}:{Line(child)}: {name} throws before mediator.Send, so the caller sees a failure that bypasses authorisation");

    private static bool ContainsSend(StatementSyntax statement, string? mediator) =>
        OwnedNodes(statement).OfType<InvocationExpressionSyntax>().Any(call => IsSend(call, mediator));

    private static bool IsSend(InvocationExpressionSyntax call, string? mediator) =>
        mediator is not null
        && CallName(call) == "Send"
        && ReceiverName(call) == mediator;

    private static string ReceiverName(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
            ? receiver.Identifier.ValueText
            : string.Empty;

    // A lambda or local function declared before the send is its own scope; a return inside it never answers
    // the request, so only the delegate's own returns count.
    private static IEnumerable<SyntaxNode> OwnedNodes(SyntaxNode node) =>
        node.DescendantNodesAndSelf(child => child is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static IEnumerable<ReturnStatementSyntax> OwnedReturns(StatementSyntax statement) =>
        OwnedNodes(statement).OfType<ReturnStatementSyntax>();
}

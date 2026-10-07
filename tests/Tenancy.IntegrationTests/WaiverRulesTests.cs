using Evidgate.Modules.Policy.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.3.1-T1 without a database: the waiver state machine and the approver ≠ requester rule
/// as pure functions (<see cref="WaiverRules"/>), and the closed scope list
/// (<see cref="WaiverScope"/>). The HTTP/DB behaviour is pinned by <see cref="WaiverTests"/>.
/// </summary>
public sealed class WaiverRulesTests
{
    [Theory]
    [InlineData(WaiverState.Requested, true)]
    [InlineData(WaiverState.Approved, false)]
    [InlineData(WaiverState.Expired, false)]
    [InlineData(WaiverState.Revoked, false)]
    public void CanApprove_OnlyFromRequested(string state, bool expected) =>
        WaiverRules.CanApprove(state).ShouldBe(expected);

    [Theory]
    [InlineData(WaiverState.Requested, true)]
    [InlineData(WaiverState.Approved, true)]
    [InlineData(WaiverState.Expired, false)]
    [InlineData(WaiverState.Revoked, false)]
    public void CanRevoke_FromRequestedOrApproved(string state, bool expected) =>
        WaiverRules.CanRevoke(state).ShouldBe(expected);

    [Fact]
    public void IsSelfApproval_IsTrueOnlyForTheSameId()
    {
        var requester = Guid.CreateVersion7();

        WaiverRules.IsSelfApproval(requester, requester).ShouldBeTrue();
        WaiverRules.IsSelfApproval(requester, Guid.CreateVersion7()).ShouldBeFalse();
    }

    [Theory]
    [InlineData(WaiverScope.Case, true)]
    [InlineData(WaiverScope.Suite, true)]
    [InlineData("run", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WaiverScope_IsKnown_OnlyForCaseAndSuite(string? scope, bool expected) =>
        WaiverScope.IsKnown(scope).ShouldBe(expected);
}

using System.Reflection;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Authoring.Application.Reports;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The grant each report handler demands: reads need authoring:read, only the create needs authoring:write.</summary>
public sealed class ReportPermissionTests
{
    [Theory]
    [InlineData(typeof(ListTestReportsQueryHandler), "authoring:read")]
    [InlineData(typeof(GetTestReportQueryHandler), "authoring:read")]
    [InlineData(typeof(GetTestReportJobQueryHandler), "authoring:read")]
    [InlineData(typeof(CreateTestReportCommandHandler), "authoring:write")]
    public void Handler_DemandsExactlyTheDocumentedPermission(Type handler, string expected)
    {
        var attributes = handler.GetCustomAttributes<PermissionAttribute>(inherit: false).ToList();

        attributes.Select(attribute => attribute.Permission).ShouldBe([expected]);
    }
}

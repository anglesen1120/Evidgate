using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>The audit rows of one export, ready to be written as JSONL or CSV.</summary>
/// <param name="Format"><c>jsonl</c> or <c>csv</c>.</param>
/// <param name="Rows">Rows oldest first.</param>
internal sealed record AuditExportFile(string Format, IReadOnlyList<AuditEntryDto> Rows);

/// <summary>
/// <c>GET /v1/orgs/{o}/audit/export</c> (02.7.1 AC5). It is a command although it reads: the export
/// itself is an audited action (<c>audit.export</c>), and running it in the read-write unit of
/// work makes the audit row and the read one transaction.
/// </summary>
internal sealed record ExportAuditCommand(Guid OrgId, string Format, AuditFilter Filter) : ICommand<Result<AuditExportFile>>;

/// <summary>Handler of <see cref="ExportAuditCommand"/>.</summary>
[Permission(Permissions.Audit.Read)]
[AuditedAction("audit.export")]
internal sealed class ExportAuditCommandHandler(ITenantDb db, ITenantScopeAccessor scopes, IAuditTrail audit)
    : ICommandHandler<ExportAuditCommand, Result<AuditExportFile>>
{
    /// <summary>Default window when no range is given.</summary>
    internal const int DefaultRangeDays = 30;

    /// <summary>Longest window an export may span (422 <c>export_range_too_large</c> beyond it).</summary>
    internal const int MaxRangeDays = 92;

    /// <summary>Most rows one export streams; a larger set must be narrowed by the range.</summary>
    internal const int MaxRows = 100_000;

    private const int PageSize = 5000;

    /// <inheritdoc />
    public async ValueTask<Result<AuditExportFile>> Handle(ExportAuditCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ExportAuditCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure<AuditExportFile>(Error.NotFound(OrgMismatch.Message));
        }

        var to = command.Filter.To ?? DateTimeOffset.UtcNow;
        var from = command.Filter.From ?? to.AddDays(-DefaultRangeDays);

        if (from >= to || (to - from).TotalDays > MaxRangeDays)
        {
            return Result.Failure<AuditExportFile>(Error.Unprocessable(
                "export_range_too_large",
                $"an export covers a range of at most {MaxRangeDays} days with from < to",
                [new ErrorDetail("from", "range")]));
        }

        var rows = new List<AuditEntryDto>();
        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;

        while (true)
        {
            var page = await db.QueryAsync<AuditPageRow>(
                AuditReadSql.Page,
                new
                {
                    org_id = orgId,
                    actor_type = command.Filter.ActorType,
                    action = command.Filter.Action,
                    object_type = command.Filter.ObjectType,
                    from,
                    to,
                    cursor_created_at = cursorCreatedAt,
                    cursor_id = cursorId,
                    limit = PageSize,
                },
                cancellationToken).ConfigureAwait(false);

            if (rows.Count + page.Count > MaxRows)
            {
                return Result.Failure<AuditExportFile>(Error.Unprocessable(
                    "export_range_too_large",
                    $"the range holds more than {MaxRows} events; narrow it",
                    [new ErrorDetail("from", "too_many_rows")]));
            }

            rows.AddRange(page.Select(row => row.ToDto()));

            if (page.Count < PageSize)
            {
                break;
            }

            cursorCreatedAt = page[^1].CreatedAt;
            cursorId = page[^1].Id;
        }

        rows.Reverse();

        audit.Describe(new AuditObject(
            "audit_log",
            orgId.ToString("D"),
            Metadata: new Dictionary<string, string>
            {
                ["format"] = command.Format,
                ["count"] = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["range_days"] = ((int)Math.Ceiling((to - from).TotalDays)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));

        return Result.Success(new AuditExportFile(command.Format, rows));
    }
}

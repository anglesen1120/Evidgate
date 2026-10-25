using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Evidence.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// Imports one file into a project's feature tree (13.1.1 §5 route 25, AC13/AC15): the raw bytes the caller uploaded,
/// the format its query string named, and whether this is a dry run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the bytes, not a parsed model.</b> The body is the file itself (CSV, XLSX or the JSON document) and the
/// <c>Idempotency-Key</c> of AC18 has to describe it: the C2 idempotency behaviour hashes the serialized command, so the
/// upload is a member of the command (base64 in that serialization) — two different files under one key are a 409
/// <c>idempotency_conflict</c>, and the same file under the same key replays the first report without a second audit row.
/// The <c>format</c> and <c>dry_run</c> members are in the command for the same reason: a key is bound to the whole
/// request, not only to its bytes.
/// </para>
/// <para>
/// <b>Parsing is in the handler, the caps are at the edge.</b> The delegate answers 413 for a body over
/// <c>ImportLimits.MaxBodyBytes</c> and 422 for a missing or unknown <c>format</c> before the pipeline runs (D-S55-Q3's
/// rule: the request's shape and its size limits are the delegate's, the values are the handler's), and this handler
/// parses the bytes it was given, so a refused file costs one tenant probe and no statement against the tree tables.
/// </para>
/// </remarks>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Format">The normalized format token: <c>csv</c>, <c>json</c> or <c>xlsx</c>.</param>
/// <param name="DryRun">True to report what the file would do without writing anything (AC13).</param>
/// <param name="Body">The uploaded file, byte for byte (at most <c>ImportLimits.MaxBodyBytes</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, AC18).</param>
internal sealed record ImportAuthoringCommand(
    Guid ProjectId,
    string Format,
    bool DryRun,
    byte[] Body,
    string IdempotencyKey) : ICommand<Result<ImportResponse>>, IIdempotentCommand
{
    /// <summary>
    /// The dedupe operation: one stable name rather than the record's full name, so a key stays bound to the import
    /// operation even if this type moves (00 §9.1's <c>idempotency_record.operation</c>).
    /// </summary>
    public string Operation => "authoring.import";
}

/// <summary>Handler of <see cref="ImportAuthoringCommand"/>: validate the whole file, then apply it as one batch.</summary>
/// <param name="db">The tenant-scoped Dapper seam (the project probe).</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the C2 project-scope rule and the version rows' authoring member.</param>
/// <param name="tenant">The request's tenant context, for the C2 project-scope rule.</param>
/// <param name="importer">The importer of §5 route 25 (whole file or nothing, one <c>batch_id</c>).</param>
/// <param name="parsing">The 06.3.1 parser profile, resolved lazily: only the XLSX format reads an archive.</param>
/// <param name="audit">The audit trail; a dry run and an all-unchanged file record nothing (AC15).</param>
/// <remarks>
/// The order of the refusals is the route's contract: the C2 project-scope rule first (403 <c>project_scope_mismatch</c>),
/// the project's visibility second (404, AC10), the file third (413 for the row cap, 422 with one detail per offending
/// row), and the importer's own whole-file refusal last — each of them leaves nothing behind, because every write the
/// importer performs rides this command's one transaction (C2 stage 3).
/// </remarks>
[AuditedAction("authoring.import")]
[Permission(Permissions.Authoring.Write)]
internal sealed class ImportAuthoringCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    AuthoringImporter importer,
    Lazy<IParsing?> parsing,
    IAuditTrail audit) : ICommandHandler<ImportAuthoringCommand, Result<ImportResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ImportResponse>> Handle(
        ImportAuthoringCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ImportAuthoringCommand));

        ArgumentNullException.ThrowIfNull(command);

        // AuthoringRules.Project first, exactly like the other write handlers: RLS scopes the tree per org, so a
        // credential narrowed to a sibling project must be refused here and not by the row filter.
        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<ImportResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false)
            is { } missingProject)
        {
            return Result.Failure<ImportResponse>(missingProject);
        }

        if (ImportFormats.Parse(command.Format, command.Body, parsing.Value, cancellationToken) is not { } parsed)
        {
            // Unreachable from the route (the delegate refuses an unknown format before the pipeline) and reachable from
            // a direct caller: an unsupported format is the same 422, never an exception.
            return Result.Failure<ImportResponse>(ImportFormats.Unsupported());
        }

        if (parsed.TooLarge)
        {
            return Result.Failure<ImportResponse>(ImportFormats.TooManyRows());
        }

        if (parsed.Issues.Count > 0)
        {
            return Result.Failure<ImportResponse>(ImportFormats.Refused(parsed.Issues));
        }

        // AC15: a row's code is how a re-import finds it; the actor is the authoring member, null for a service caller.
        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);

        var imported = await importer.ImportAsync(
            orgId,
            command.ProjectId,
            actor,
            parsed.Rows,
            command.DryRun,
            cancellationToken).ConfigureAwait(false);

        if (!imported.IsSuccess)
        {
            // The importer's whole-file refusal (unknown_parent, code_conflict, steps_invalid, …) already carries its
            // 422 and one detail per offending row; the route surfaces it unchanged.
            return Result.Failure<ImportResponse>(imported.Error!);
        }

        var report = imported.Value!;

        if (report.BatchId is { } batchId)
        {
            audit.Describe(new AuditObject("authoring_import", batchId.ToString("D")));
        }
        else
        {
            // AC15: a dry run and a file whose every row is unchanged wrote nothing, so this request appended no audit
            // row either — the same suppression the no-op PATCH uses (plan note at :541).
            audit.Suppress();
        }

        return Result.Success(new ImportResponse(
            report.BatchId,
            new ImportCountsDto(
                report.Counts.Created,
                report.Counts.Updated,
                report.Counts.Restored,
                report.Counts.Unchanged),
            [.. report.Rows.Select(row => new ImportRowDto(
                row.Index,
                row.Key,
                ImportFormats.KindName(row.Kind),
                row.Outcome,
                row.Code))]));
    }
}

/// <summary>
/// The one place the import/export route turns a query-string token into a format, reads a file with that format's
/// reader and writes it with the same format's writer, so the three formats share one vocabulary (13.1.1 §5 route 25).
/// </summary>
/// <remarks>
/// <b>XLSX needs the 06.3.1 parser profile.</b> Reading a workbook means reading a zip under caps, which is
/// <c>IParsing</c>'s job (the sanctioned route across modules, 00 §5.5 C7); the module resolves that service lazily, so a
/// host that composes Authoring without Evidence still starts and still serves CSV and JSON. In that host — and in any
/// host whose parser refuses the archive — the XLSX format answers the same 422
/// <c>request_invalid</c> <c>/format</c> <c>unsupported_format</c> as an unknown token, never a 500 and never a silent
/// CSV interpretation.
/// </remarks>
internal static class ImportFormats
{
    /// <summary>The CSV format token.</summary>
    internal const string Csv = "csv";

    /// <summary>The JSON format token.</summary>
    internal const string Json = "json";

    /// <summary>The XLSX format token.</summary>
    internal const string Xlsx = "xlsx";

    /// <summary>The CSV media type of an export, with the encoding a spreadsheet expects.</summary>
    internal const string CsvContentType = "text/csv; charset=utf-8";

    /// <summary>The JSON media type of the import document and of the JSON export.</summary>
    internal const string JsonContentType = "application/json";

    /// <summary>The XLSX media type (the only one <c>Results.Stream</c> may declare without a charset).</summary>
    internal const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The 413 of a file over <see cref="ImportLimits.MaxDataRows"/> data rows.</summary>
    private static readonly Error TooLarge = new(
        ApiErrorCode.PayloadTooLarge,
        string.Create(CultureInfo.InvariantCulture, $"an import carries at most {ImportLimits.MaxDataRows} rows"),
        StatusCodes.Status413PayloadTooLarge);

    /// <summary>Reads a <c>?format=</c> token, or null when it names no format this route serves.</summary>
    /// <param name="raw">The raw query value.</param>
    /// <returns><c>csv</c>, <c>json</c>, <c>xlsx</c>, or null.</returns>
    internal static string? ReadFormat(string? raw) => raw?.Trim() switch
    {
        Csv => Csv,
        Json => Json,
        Xlsx => Xlsx,
        _ => null,
    };

    /// <summary>The 422 of a missing or unknown <c>format</c> (and of an XLSX upload no composed host can read).</summary>
    internal static Error Unsupported() => Error.Invalid(
        "the format must be csv, json or xlsx",
        [new ErrorDetail("/format", "unsupported_format")]);

    /// <summary>The 413 of a file over the row cap.</summary>
    internal static Error TooManyRows() => TooLarge;

    /// <summary>
    /// The 422 of a refused file: one detail per offending row (its 1-based index, a fixed member pointer and the
    /// AC13/AC16 reason), never a cell value, capped at the same 100 details the readers stop at.
    /// </summary>
    /// <param name="issues">The reader's issues, in file order.</param>
    internal static Error Refused(IReadOnlyList<ImportIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        return Error.Invalid(
            "the file cannot be imported; nothing was written",
            [.. issues.Take(100).Select(issue => new ErrorDetail(issue.Path, issue.Reason, issue.Index))]);
    }

    /// <summary>Reads the import route's <c>?dry_run=</c> flag: absent or empty means a real run.</summary>
    /// <param name="raw">The raw query value.</param>
    /// <param name="dryRun">True for <c>true</c>, false for an absent value or <c>false</c>.</param>
    /// <returns>The 422 of any other token, naming <c>/dry_run</c>.</returns>
    internal static Error? DryRun(string? raw, out bool dryRun)
    {
        dryRun = false;

        switch (raw?.Trim())
        {
            case null or "" or "false":
                return null;
            case "true":
                dryRun = true;
                return null;
            default:
                return Error.Invalid(
                    "dry_run must be true or false",
                    [new ErrorDetail("/dry_run", "value_invalid")]);
        }
    }

    /// <summary>
    /// Reads the export route's <c>?include=</c> list: a comma list of <c>nodes</c>, <c>items</c> and <c>cases</c>, absent
    /// or empty meaning all three (13.1.1 §5 route 25). A repeated token is idempotent, an unknown or empty token is the
    /// 422 naming <c>/include</c>.
    /// </summary>
    /// <param name="raw">The raw query value.</param>
    /// <param name="include">The kinds to emit.</param>
    /// <returns>The 422 of an unknown token, or null.</returns>
    internal static Error? Include(string? raw, out ImportInclude include)
    {
        include = ImportInclude.None;

        if (raw is null || raw.Trim().Length == 0)
        {
            include = ImportInclude.All;

            return null;
        }

        foreach (var token in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            switch (token)
            {
                case "nodes":
                    include |= ImportInclude.Nodes;
                    break;
                case "items":
                    include |= ImportInclude.Items;
                    break;
                case "cases":
                    include |= ImportInclude.Cases;
                    break;
                default:
                    include = ImportInclude.None;

                    return Error.Invalid(
                        "include is a comma list of nodes, items and cases",
                        [new ErrorDetail("/include", "value_invalid")]);
            }
        }

        return null;
    }

    /// <summary>The wire name of a row kind (<c>node</c>, <c>item</c>, <c>case</c>).</summary>
    /// <param name="kind">The kind.</param>
    internal static string KindName(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => "node",
        ImportRowKind.Item => "item",
        _ => "case",
    };

    /// <summary>Parses one file with the reader of <paramref name="format"/>.</summary>
    /// <param name="format">A normalized format token (<see cref="ReadFormat"/>).</param>
    /// <param name="body">The file's bytes.</param>
    /// <param name="parsing">The parser profile, or null when the host composes no Evidence module.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The parse, or null when no reader serves <paramref name="format"/> in this host.</returns>
    internal static ImportParse? Parse(string format, byte[] body, IParsing? parsing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(body);

        return format switch
        {
            Csv => CsvFormat.Read(body),
            Json => JsonFormat.Read(body),
            Xlsx => parsing is null ? null : XlsxFormat.Read(body, parsing, cancellationToken),
            _ => null,
        };
    }

    /// <summary>Writes an export with the writer of <paramref name="format"/>.</summary>
    /// <param name="format">A normalized format token (<see cref="ReadFormat"/>).</param>
    /// <param name="rows">The exported rows, in export order.</param>
    /// <param name="output">The response body's stream.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the file is written.</returns>
    internal static Task WriteAsync(
        string format,
        IReadOnlyList<ImportRow> rows,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(output);

        return format switch
        {
            Csv => CsvFormat.WriteAsync(rows, output, cancellationToken),
            Xlsx => XlsxFormat.WriteAsync(rows, output, cancellationToken),
            _ => JsonFormat.WriteAsync(rows, output, cancellationToken),
        };
    }

    /// <summary>The declared media type of an export file.</summary>
    /// <param name="format">A normalized format token.</param>
    internal static string ContentType(string format) => format switch
    {
        Csv => CsvContentType,
        Xlsx => XlsxContentType,
        _ => JsonContentType,
    };

    /// <summary>
    /// The fixed ASCII attachment name of an export (<c>authoring-export.csv</c>, <c>.json</c>, <c>.xlsx</c>): never the
    /// project's name, never a posted value (S3).
    /// </summary>
    /// <param name="format">A normalized format token.</param>
    internal static string FileName(string format) => "authoring-export." + format;
}

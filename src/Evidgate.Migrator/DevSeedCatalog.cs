using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Migrator;

/// <summary>
/// The catalog half of <c>seed-dev</c>: one test case and one suite with one revision in <c>dev-project</c>, written
/// through the Catalog module's own registration service and commands, so a manifest has a suite revision to name.
/// </summary>
/// <remarks>
/// A run uploads a manifest only against a suite revision that exists in the project, and nothing else creates catalog
/// rows in a fresh dev stack. The content below is fixed, so a rerun finds the same case, the same case revision, the
/// same suite and the same revision content and adds nothing; no catalog row or hash is composed by hand. The ids a
/// manifest needs are printed, because the revision id is the database's and changes only with the volume.
/// </remarks>
internal static class DevSeedCatalog
{
    /// <summary>The suite's slug in <c>dev-project</c>; the rerun looks the suite up by it.</summary>
    internal const string SuiteSlug = "dev-seed-smoke";

    /// <summary>The suite's display name.</summary>
    internal const string SuiteName = "Dev seed smoke";

    /// <summary>The stdout prefix of the line that carries the two ids a manifest names.</summary>
    internal const string ManifestLine = "seed-dev: manifest inputs";

    /// <summary>The annotation id that gives the sample case its identity, so a rerun registers the same case.</summary>
    internal static readonly Guid CaseAnnotationId = Guid.Parse("0198a000-0000-7000-8000-00000000d008");

    /// <summary>The repository path of the sample test.</summary>
    internal const string CasePath = "tests/dev-seed.spec.ts";

    /// <summary>The commit the sample tree is synced at: any 7-64 hex string will do, and a fixed one keeps the row stable.</summary>
    private const string CaseCommit = "0198a00000007000800000000000d008";

    /// <summary>
    /// The sample test: one <c>evidgate.id</c> annotation and one test, so case discovery reads a test case whose name,
    /// content hash and revision hash stay the same across runs.
    /// </summary>
    private const string CaseContent =
        "// evidgate.id: 0198a000-0000-7000-8000-00000000d008\n" +
        "test('dev seed smoke', () => { expect(1).toBe(1); });\n";

    /// <summary>Idempotency keys: a fixed key makes a replay return the stored answer instead of creating a second row.</summary>
    private const string SuiteKey = "dev-seed-suite";

    private const string RevisionKey = "dev-seed-suite-revision";

    /// <summary>Page size of the project's case and suite reads: the list endpoints' maximum, so a small project is one page.</summary>
    private const int PageSize = 200;

    /// <summary>Writes the case, the suite and its revision, then prints the two ids a manifest names.</summary>
    internal static async Task<int> WriteAsync(
        IMediator mediator,
        IServiceProvider services,
        TextWriter output,
        TextWriter error)
    {
        var (caseRevisionId, caseExit) = await CaseRevisionAsync(mediator, services, error).ConfigureAwait(false);
        if (caseRevisionId is not { } revisionId)
        {
            return caseExit;
        }

        var (suiteId, suiteExit) = await EnsureSuiteAsync(mediator, error).ConfigureAwait(false);
        if (suiteId is not { } suite)
        {
            return suiteExit;
        }

        var (head, revisionExit) = await EnsureRevisionAsync(mediator, suite, revisionId, error).ConfigureAwait(false);
        if (head is not { } accepted || accepted.SuiteRevisionId is not { } suiteRevisionId)
        {
            return revisionExit;
        }

        var outcome = accepted.Created ? "created" : "already present";
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"seed-dev: catalog suite {SuiteSlug} revision {accepted.RevisionNo} {accepted.RevisionHash} ({outcome})")).ConfigureAwait(false);

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{ManifestLine} suite_revision_id={suiteRevisionId} case_revision_id={revisionId}")).ConfigureAwait(false);

        return MigratorExitCodes.Success;
    }

    /// <summary>
    /// The sample case's revision: the already-registered case is looked up first and written only when it is absent.
    /// </summary>
    /// <remarks>
    /// The registration service takes the <em>complete</em> tree of a commit and marks every active case absent from it
    /// missing, so registering the one-file sample in a project that already holds cases would sweep them. The lookup
    /// therefore comes first, and a project with any other active case is refused instead of swept; the write runs only
    /// for an empty project, where the sweep has nothing to destroy, and a case that a concurrent sync commits between
    /// that check and the write is refused the same way, with the sweep rolled back. The revision id is the database's,
    /// so it is read back through the module's queries, never composed.
    /// </remarks>
    private static async Task<(Guid? CaseRevisionId, int ExitCode)> CaseRevisionAsync(
        IMediator mediator,
        IServiceProvider services,
        TextWriter error)
    {
        var found = await FindCaseAsync(mediator, error).ConfigureAwait(false);
        if (found.ExitCode is { } failed)
        {
            return (null, failed);
        }

        if (found.CaseId is null)
        {
            if (await OtherActiveCaseAsync(mediator, error).ConfigureAwait(false) is { } blocked)
            {
                return (null, blocked);
            }

            if (await RegisterCaseAsync(services, error).ConfigureAwait(false) is { } refused)
            {
                return (null, refused);
            }

            found = await FindCaseAsync(mediator, error).ConfigureAwait(false);
            if (found.ExitCode is { } afterWrite)
            {
                return (null, afterWrite);
            }

            if (found.CaseId is null)
            {
                await error.WriteLineAsync("E seed-dev: the sample case was registered yet cannot be listed back").ConfigureAwait(false);

                return (null, MigratorExitCodes.MigrationFailed);
            }
        }

        var revisions = await mediator.Send(new GetCaseRevisionsQuery(found.CaseId.Value, null, 1)).ConfigureAwait(false);
        if (!revisions.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the sample case revisions could not be read ({revisions.Error!.Code}): {revisions.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        if (revisions.Value.Items.Count == 0)
        {
            await error.WriteLineAsync("E seed-dev: the sample case has no revision after its registration").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        return (revisions.Value.Items[0].Id, MigratorExitCodes.Success);
    }

    /// <summary>
    /// The dev project's sample case, found by its annotation id across every page of the keyset list: the case a dev
    /// project created first need not be on page one. Null with no exit code when the project has no such case.
    /// </summary>
    private static async Task<(Guid? CaseId, int? ExitCode)> FindCaseAsync(IMediator mediator, TextWriter error)
    {
        string? cursor = null;
        do
        {
            var listed = await mediator.Send(new ListCasesQuery(DevSeed.ProjectId, null, null, cursor, PageSize)).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                await error.WriteLineAsync(
                    $"E seed-dev: the dev project cases could not be read ({listed.Error!.Code}): {listed.Error.Message}").ConfigureAwait(false);

                return (null, MigratorExitCodes.MigrationFailed);
            }

            if (listed.Value.Items.FirstOrDefault(item => item.AnnotationId == CaseAnnotationId) is { } found)
            {
                return (found.Id, null);
            }

            cursor = listed.Value.NextCursor;
        }
        while (cursor is not null);

        return (null, null);
    }

    /// <summary>
    /// Refuses the sample's registration when the dev project already holds an active case: the one-file tree is not
    /// that project's tree, and registering it would mark the other cases missing. The check asks for one active row,
    /// which is all the refusal needs, so it does not page.
    /// </summary>
    private static async Task<int?> OtherActiveCaseAsync(IMediator mediator, TextWriter error)
    {
        var listed = await mediator.Send(new ListCasesQuery(DevSeed.ProjectId, CaseStatus.Active, null, null, 1)).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev project cases could not be read ({listed.Error!.Code}): {listed.Error.Message}").ConfigureAwait(false);

            return MigratorExitCodes.MigrationFailed;
        }

        if (listed.Value.Items.Count == 0)
        {
            return null;
        }

        await error.WriteLineAsync(
            "E seed-dev: the dev project already holds active cases, and the sample case's registration would mark them missing; seed an empty project instead (scripts/dev/down.sh && scripts/dev/up.sh --reset)").ConfigureAwait(false);

        return MigratorExitCodes.MigrationFailed;
    }

    /// <summary>Registers the one-file sample tree through the case-identity service a catalog sync uses; null on success.</summary>
    private static async Task<int?> RegisterCaseAsync(IServiceProvider services, TextWriter error)
    {
        var scopes = services.GetRequiredService<ITenantScopeFactory>();
        var cases = services.GetRequiredService<ICaseIdentityService>();
        SourceFile[] files = [new(CasePath, CaseContent, "playwright", "typescript")];

        await using var scope = await scopes.BeginAsync(DevSeed.OrgId, null, readOnlyTransaction: false).ConfigureAwait(false);

        var outcome = await cases.RegisterDiscoveredAsync(DevSeed.ProjectId, CaseCommit, files, CancellationToken.None)
            .ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            await error.WriteLineAsync($"E seed-dev: the sample case could not be registered ({outcome.Failure})").ConfigureAwait(false);

            return MigratorExitCodes.MigrationFailed;
        }

        // A catalog sync that commits between the guard and this write adds active cases the one-file tree does not
        // hold; the service reports them as newly missing, so committing would sweep them. The transaction is rolled
        // back by its disposal instead, leaving those cases untouched.
        if (outcome.Report!.Missing > 0)
        {
            await error.WriteLineAsync(
                $"E seed-dev: {outcome.Report.Missing} other active case(s) appeared in the dev project while the sample was registered; the sweep was rolled back and nothing was written").ConfigureAwait(false);

            return MigratorExitCodes.MigrationFailed;
        }

        await scope.CommitAsync().ConfigureAwait(false);

        return null;
    }

    /// <summary>The suite's id: the existing one when the slug is taken, else the one the real command creates.</summary>
    private static async Task<(Guid? SuiteId, int ExitCode)> EnsureSuiteAsync(IMediator mediator, TextWriter error)
    {
        string? cursor = null;
        do
        {
            var listed = await mediator.Send(new ListSuitesQuery(DevSeed.ProjectId, cursor, PageSize)).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                await error.WriteLineAsync(
                    $"E seed-dev: the dev project suites could not be read ({listed.Error!.Code}): {listed.Error.Message}").ConfigureAwait(false);

                return (null, MigratorExitCodes.MigrationFailed);
            }

            if (listed.Value.Items.FirstOrDefault(suite => string.Equals(suite.Slug, SuiteSlug, StringComparison.Ordinal)) is { } existing)
            {
                return (existing.Id, MigratorExitCodes.Success);
            }

            cursor = listed.Value.NextCursor;
        }
        while (cursor is not null);

        var created = await mediator.Send(new CreateSuiteCommand(DevSeed.ProjectId, SuiteName, SuiteSlug, SuiteKey)).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev suite was refused ({created.Error!.Code}): {created.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        return (created.Value.Id, MigratorExitCodes.Success);
    }

    /// <summary>
    /// The suite's revision whose content is the sample case as its only member. The preview plans the same content and
    /// hash the create command stores, so a revision that already holds it is returned without a write; submitting that
    /// content to the create command would treat it as a revert and move the suite's head off a newer revision.
    /// </summary>
    private static async Task<(SuiteRevisionHead? Head, int ExitCode)> EnsureRevisionAsync(
        IMediator mediator,
        Guid suiteId,
        Guid caseRevisionId,
        TextWriter error)
    {
        var body = new CreateSuiteRevisionRequest(
            [new SuiteMemberInput(null, caseRevisionId, null, null)],
            Edges: null,
            ShardStrategy: null,
            RetryPolicy: null,
            EnvironmentRequirements: null);

        var preview = await mediator.Send(new PreviewSuiteRevisionQuery(suiteId, body)).ConfigureAwait(false);
        if (!preview.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev suite revision could not be planned ({preview.Error!.Code}): {preview.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        if (preview.Value.SuiteRevisionId is not null)
        {
            return (preview.Value with { Created = false, HeadMoved = false }, MigratorExitCodes.Success);
        }

        var appended = await mediator.Send(new CreateSuiteRevisionCommand(suiteId, body, RevisionKey)).ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev suite revision was refused ({appended.Error!.Code}): {appended.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        return (appended.Value, MigratorExitCodes.Success);
    }
}

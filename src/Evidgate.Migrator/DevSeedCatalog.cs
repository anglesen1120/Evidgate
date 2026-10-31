using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Application.Queries;
using Evidgate.Modules.Catalog.Contracts;
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
    /// Registers the sample test through the case-identity service a catalog sync uses, then reads the case revision
    /// back through the module's queries: the revision id is the database's, so it is looked up, never composed.
    /// </summary>
    private static async Task<(Guid? CaseRevisionId, int ExitCode)> CaseRevisionAsync(
        IMediator mediator,
        IServiceProvider services,
        TextWriter error)
    {
        var scopes = services.GetRequiredService<ITenantScopeFactory>();
        var cases = services.GetRequiredService<ICaseIdentityService>();
        SourceFile[] files = [new(CasePath, CaseContent, "playwright", "typescript")];

        // A sync is a write on its own: one tenant transaction, committed only when the whole tree was accepted.
        await using (var scope = await scopes.BeginAsync(DevSeed.OrgId, null, readOnlyTransaction: false).ConfigureAwait(false))
        {
            var outcome = await cases.RegisterDiscoveredAsync(DevSeed.ProjectId, CaseCommit, files, CancellationToken.None)
                .ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                await error.WriteLineAsync(
                    $"E seed-dev: the sample case was refused ({outcome.Failure}); the dev project may already hold cases this one-file tree would mark missing").ConfigureAwait(false);

                return (null, MigratorExitCodes.MigrationFailed);
            }

            await scope.CommitAsync().ConfigureAwait(false);
        }

        if (await FindCaseAsync(mediator, error).ConfigureAwait(false) is not { } caseId)
        {
            return (null, MigratorExitCodes.MigrationFailed);
        }

        var revisions = await mediator.Send(new GetCaseRevisionsQuery(caseId, null, 1)).ConfigureAwait(false);
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

    /// <summary>The dev project's sample case id, found by its fixed annotation id.</summary>
    private static async Task<Guid?> FindCaseAsync(IMediator mediator, TextWriter error)
    {
        var listed = await mediator.Send(new ListCasesQuery(DevSeed.ProjectId, null, null, null, 50)).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev project cases could not be read ({listed.Error!.Code}): {listed.Error.Message}").ConfigureAwait(false);

            return null;
        }

        var found = listed.Value.Items.FirstOrDefault(item => item.AnnotationId == CaseAnnotationId);
        if (found is null)
        {
            await error.WriteLineAsync("E seed-dev: the sample case was registered yet cannot be listed back").ConfigureAwait(false);
        }

        return found?.Id;
    }

    /// <summary>The suite's id: the existing one when the slug is taken, else the one the real command creates.</summary>
    private static async Task<(Guid? SuiteId, int ExitCode)> EnsureSuiteAsync(IMediator mediator, TextWriter error)
    {
        var listed = await mediator.Send(new ListSuitesQuery(DevSeed.ProjectId, null, 50)).ConfigureAwait(false);
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
    /// The suite's revision with the sample case as its only member. Identical content returns the stored revision
    /// (same id, same hash, nothing appended), which is what makes the seed re-runnable.
    /// </summary>
    private static async Task<(SuiteRevisionHead? Head, int ExitCode)> EnsureRevisionAsync(
        IMediator mediator,
        Guid suiteId,
        Guid caseRevisionId,
        TextWriter error)
    {
        // The head is read before the command: a repeated Idempotency-Key replays the first answer, so its Created
        // flag stays true on a rerun and the head moving is the only evidence that this run appended a revision.
        var before = await mediator.Send(new GetSuiteQuery(suiteId)).ConfigureAwait(false);
        if (!before.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev suite could not be read ({before.Error!.Code}): {before.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        var previousHead = before.Value.HeadRevisionId;

        var body = new CreateSuiteRevisionRequest(
            [new SuiteMemberInput(null, caseRevisionId, null, null)],
            Edges: null,
            ShardStrategy: null,
            RetryPolicy: null,
            EnvironmentRequirements: null);

        var appended = await mediator.Send(new CreateSuiteRevisionCommand(suiteId, body, RevisionKey)).ConfigureAwait(false);
        if (!appended.IsSuccess)
        {
            await error.WriteLineAsync(
                $"E seed-dev: the dev suite revision was refused ({appended.Error!.Code}): {appended.Error.Message}").ConfigureAwait(false);

            return (null, MigratorExitCodes.MigrationFailed);
        }

        var head = appended.Value;

        return (head.SuiteRevisionId is { } revisionId && revisionId != previousHead
            ? head
            : head with { Created = false }, MigratorExitCodes.Success);
    }
}

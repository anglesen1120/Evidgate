// The statement half of the evaluation schema cases: the one evaluation INSERT, its refusals, and the generic
// "run this statement and return the server's answer" helpers — each statement in its own transaction, so a case
// asserts exactly what the server said to that one statement.
using Npgsql;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationSchemaTests
{
    private const string InsertSql =
        """
        insert into public.evaluation
          (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
           missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
        values
          (@id, @org_id, @run_id, @evidence_revision, @policy_revision_id, @policy_hash, @manifest_hash,
           @completeness, @missing, @outcome, @decision, @reasons, @evaluator_version, @mode, @exceptions_hash,
           now())
        """;

    private static (string Name, object Value)[] Parameters(EvaluationSpec spec) =>
    [
        ("id", spec.Id),
        ("org_id", spec.RowOrg),
        ("run_id", spec.RunId),
        ("evidence_revision", spec.EvidenceRevision),
        ("policy_revision_id", spec.PolicyRevisionId),
        ("policy_hash", spec.PolicyHash),
        ("manifest_hash", spec.ManifestHash),
        ("completeness", spec.Completeness),
        ("missing", spec.Missing),
        ("outcome", spec.Outcome),
        ("decision", spec.Decision),
        ("reasons", spec.Reasons),
        ("evaluator_version", spec.EvaluatorVersion),
        ("mode", spec.Mode),
        ("exceptions_hash", spec.ExceptionsHash),
    ];

    /// <summary>One evaluation INSERT as the app role in its own committed transaction.</summary>
    private async Task<int> InsertAsync(RunCreationTests.World world, Guid org, EvaluationSpec spec)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        var affected = await scope.ExecuteAsync(InsertSql, Parameters(spec));
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>One evaluation INSERT as the app role, returning the server error instead of throwing.</summary>
    private async Task<PostgresException> CaptureInsertErrorAsync(
        RunCreationTests.World world,
        Guid org,
        EvaluationSpec spec)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(InsertSql, Parameters(spec));
    }

    /// <summary>One evaluation INSERT as the app role, returning either the row count or the server error.</summary>
    private async Task<(int Affected, PostgresException? Error)> InsertOrErrorAsync(
        RunCreationTests.World world,
        Guid org,
        EvaluationSpec spec)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        try
        {
            var affected = await scope.ExecuteAsync(InsertSql, Parameters(spec));
            await scope.CommitAsync();

            return (affected, null);
        }
        catch (PostgresException error)
        {
            return (0, error);
        }
    }

    /// <summary>Runs one statement as the app role inside a fresh tenant scope and returns the server error.</summary>
    private async Task<PostgresException> CaptureAppErrorAsync(
        RunCreationTests.World world,
        Guid org,
        string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>Runs one statement as the owner inside a fresh transaction and returns the server error.</summary>
    private async Task<PostgresException> CaptureOwnerErrorAsync(
        RunCreationTests.World world,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        return await scope.CaptureErrorAsync(sql, parameters);
    }

    /// <summary>Runs one scalar read as the app role for <paramref name="org"/>.</summary>
    private async Task<T> ScalarAsOrgAsync<T>(RunCreationTests.World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.ScalarAsync<T>(sql);
    }
}

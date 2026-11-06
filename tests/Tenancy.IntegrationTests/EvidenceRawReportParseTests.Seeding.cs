// The parent rows a `raw_report_parse` test needs: one tenant with a run, the run's one planned execution, and a
// `ready` artifact whose final object the store really holds. Kept apart from the tests so the file that states what
// each case proves stays readable.
using System.Globalization;
using Evidgate.Modules.Evidence.Domain;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    /// <summary>
    /// One planned execution for the run, pointing at the case and revision <see cref="RunRowSeeder"/> seeds (name
    /// <c>probe</c>, path <c>a</c>): the report's bare-name testcase is the one hint that must resolve.
    /// </summary>
    private async Task SeedPlanAsync(World world)
    {
        var revision = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.test_revision where org_id = '{world.Org:D}' and file_path = 'a'");

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.planned_execution "
            + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.RunId:D}', '{ExecutionKey}', "
            + $"'{revision:D}', 'sha256:{new string('b', 64)}', 'staging', 'shard-1')");
    }

    /// <summary>One <c>ready</c> <c>raw_report</c> artifact whose final object the store really holds.</summary>
    private async Task<Guid> SeedArtifactAsync(World world, byte[] body)
    {
        var artifactId = Guid.CreateVersion7();
        var key = ObjectKeyFor(world, artifactId);
        var checksum = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(body));

        await _store.PutAsync(key, body);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, "
            + "media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) values "
            + $"('{artifactId:D}', '{world.Org:D}', '{world.Project:D}', '{world.RunId:D}', '{ExecutionKey}', 1, "
            + $"'{ArtifactKind.RawReport}', '{key}', '{XmlType}', '{checksum}', 'normal', 'pending', "
            + $"'{ArtifactState.Ready}', {body.LongLength.ToString(CultureInfo.InvariantCulture)}, null)");

        return artifactId;
    }

    /// <summary>Replaces the stored bytes of an artifact, the way a re-upload of the same coordinate would.</summary>
    private async Task StoreAsync(World world, Guid artifactId, byte[] body) =>
        await _store.PutAsync(ObjectKeyFor(world, artifactId), body);

    /// <summary>The final key the artifact row mints, so a test writes and the handler reads the same object.</summary>
    private static string ObjectKeyFor(World world, Guid artifactId) =>
        $"org/{world.Org:D}/project/{world.Project:D}/run/{world.RunId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";
}

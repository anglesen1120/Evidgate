// The rows one scan case needs: a ready artifact whose bytes the store really holds, the kind policy its read resolves,
// and the final key those coordinates mint. Kept beside the cases that build worlds, not in the class's main file.
using System.Globalization;
using Evidgate.Modules.Evidence.Domain;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceSecretScanTaskTests
{
    /// <summary>One <c>ready</c> artifact whose final object the store really holds.</summary>
    private Task<Seeded> SeedReadyAsync(World world, byte[] body, bool storeBody = true) =>
        SeedAsync(world, ArtifactState.Ready, body, storeBody);

    /// <summary>
    /// Inserts one artifact row as the migration role and puts the bytes at its final key, so the handler's read is a
    /// real store read. <c>scan_state</c> is <c>pending</c> and <c>sensitivity</c> <c>normal</c>, exactly as the
    /// completion leaves a verified artifact.
    /// </summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="state">The row's <c>artifact.state</c>.</param>
    /// <param name="body">The bytes of the object and the row's declared length.</param>
    /// <param name="storeBody">False to leave the object absent, for the store-failure case.</param>
    /// <param name="projectId">
    /// The project the row names, or null for the run's own: no key ties the two columns together, so a row of another
    /// project lets a case prove which one the read asks about.
    /// </param>
    private async Task<Seeded> SeedAsync(World world, string state, byte[] body, bool storeBody, Guid? projectId = null)
    {
        var artifactId = Guid.CreateVersion7();
        var owner = projectId ?? world.Project;
        var key = MintedKey(world, owner, artifactId);
        var checksum = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));

        if (storeBody)
        {
            await _store.PutAsync(key, body);
        }

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) "
            + $"values ('{artifactId:D}', '{world.Org:D}', '{owner:D}', '{world.RunId:D}', "
            + $"'{ExecutionKey}', 1, '{ArtifactKind.RawReport}', '{key}', '{XmlType}', "
            + $"'{checksum}', 'normal', 'pending', '{state}', {body.LongLength}, null)");

        return new Seeded(artifactId, key);
    }

    /// <summary>
    /// One <c>artifact_kind_policy</c> row for <c>raw_report</c>, so the handler's cap and its rejection switch are the
    /// row's: the resolution takes the lower of row and default for the cap and the row's flag for the policy.
    /// </summary>
    /// <param name="world">The tenant the policy belongs to.</param>
    /// <param name="rejectOnSecret">The project's switch.</param>
    /// <param name="projectId">The project the row names, or null for the run's own.</param>
    /// <param name="maxBytes">The row's cap.</param>
    private Task SeedKindPolicyAsync(
        World world,
        bool rejectOnSecret,
        Guid? projectId = null,
        long maxBytes = 1024 * 1024) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, reject_on_secret) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{projectId ?? world.Project:D}', '{ArtifactKind.RawReport}', "
            + $"{maxBytes.ToString(CultureInfo.InvariantCulture)}, array['{XmlType}'], 'normal', "
            + $"{rejectOnSecret.ToString().ToLowerInvariant()})");

    /// <summary>The final key the row's own coordinates mint, rendered as <c>ArtifactKeyMinter.Mint</c> does.</summary>
    /// <param name="world">The tenant and run the artifact belongs to.</param>
    /// <param name="projectId">The project the row names, which the key carries.</param>
    /// <param name="artifactId">The artifact's id.</param>
    private static string MintedKey(World world, Guid projectId, Guid artifactId) =>
        $"org/{world.Org:D}/project/{projectId:D}/run/{world.RunId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";
}

#pragma warning disable CA1861
using System.Text.Json;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.2-T3.2: <c>contracts/evidence/artifact-kind-defaults.v1.json</c> and the code carry the same table — the
/// same four kinds with the same caps and the same media types (order-insensitive: the JSON array order is not a
/// contract), the same request-body limit, the exact <c>unmapped</c> entries, and a <c>no_default</c> list that is
/// exactly the 00 §7 closed kind list minus the defaults. Divergence would mean the published contract and the
/// running code disagree about what a project accepts. Pure unit test — no container and no database, so it stays
/// on the non-Docker side of <c>verify.sh</c>'s <c>--filter-not-trait "Category=Docker"</c>.
/// </summary>
public sealed class EvidenceKindDefaultsContractTests
{
    /// <summary>
    /// 06.1.2-T3.2: the committed contract and <see cref="ArtifactKindDefaults"/> agree kind-for-kind; the
    /// <c>unmapped</c> object is exactly D-A9's <c>log</c> (20 MiB) and <c>html_report</c> (100 MiB, folded into
    /// <c>raw_report</c>); and the request-body cap is the code's published limit.
    /// </summary>
    [Fact]
    public void KindDefaultsContract_IsTheCodeTable()
    {
        var path = Path.Combine(RepositoryRoot(), "contracts", "evidence", "artifact-kind-defaults.v1.json");
        File.Exists(path).ShouldBeTrue($"the kind-defaults contract must be committed at {path} (06.1.2-T3.2)");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        root.GetProperty("request_body_max_bytes").GetInt32().ShouldBe(UploadUrlLimits.MaxBodyBytes);

        var kinds = root.GetProperty("kinds");
        var published = kinds.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal).ToList();
        published.ShouldBe(ArtifactKindDefaults.Kinds.Order(StringComparer.Ordinal).ToList());

        foreach (var kind in ArtifactKindDefaults.Kinds)
        {
            var entry = kinds.GetProperty(kind);
            var code = ArtifactKindDefaults.All[kind];

            entry.GetProperty("max_bytes").GetInt64().ShouldBe(code.MaxBytes, $"{kind} max_bytes");
            entry.GetProperty("media_types").EnumerateArray().Select(value => value.GetString()!)
                .Order(StringComparer.Ordinal)
                .ShouldBe(code.MediaTypes.Order(StringComparer.Ordinal).ToList(), ignoreOrder: false, $"{kind} media_types");
        }

        // The contract records what D-A9 fixed but the 00 §7 closed list does not implement (the `log` cap and the
        // `html_report` fold), so the `unmapped` object is exactly those two keys with their published caps.
        var unmapped = root.GetProperty("unmapped");
        unmapped.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal).ToList()
            .ShouldBe(new List<string> { "html_report", "log" });
        unmapped.GetProperty("log").GetProperty("max_bytes").GetInt64().ShouldBe(20971520, "log max_bytes");
        unmapped.GetProperty("html_report").GetProperty("max_bytes").GetInt64().ShouldBe(104857600, "html_report max_bytes");

        root.GetProperty("no_default").EnumerateArray().Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ShouldBe(
                ArtifactKind.All.Except(ArtifactKindDefaults.Kinds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>The repository root: the directory holding <c>Evidgate.slnx</c> above the test binary (the SeaweedFS fixture's rule).</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above {AppContext.BaseDirectory}");
    }
}

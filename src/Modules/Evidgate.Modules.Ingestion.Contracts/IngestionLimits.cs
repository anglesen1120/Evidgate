using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Ingestion.Contracts;

/// <summary>
/// The ingestion budgets, bound from <c>EVIDGATE:Ingestion</c> and validated at boot like every other
/// <c>EVIDGATE__*</c> group.
/// <para>
/// It lives in the module's contracts so the run-create endpoint (manifest bytes), the run-create handler
/// (manifest executions and the variant axis) and the report parser read one set of numbers. The defaults are
/// the published caps: they travel to <c>contracts/openapi.yaml</c> as <c>x-evidgate-limits</c> and a drift test
/// keeps the two in step, so a number copied from here can never quietly disagree with the published value.
/// </para>
/// </summary>
public sealed class IngestionLimits
{
    /// <summary>Configuration section bound to <c>EVIDGATE__INGESTION__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Ingestion";

    /// <summary>Published default: the most bytes one manifest body may carry, uncompressed.</summary>
    public const int DefaultMaxManifestBytes = 32 * 1024 * 1024;

    /// <summary>Published default: the most planned executions one manifest may declare.</summary>
    public const int DefaultMaxManifestExecutions = 150_000;

    /// <summary>
    /// The ceiling a deployment may raise <see cref="MaxManifestExecutions"/> to. The manifest schema admits this
    /// count, so the configured cap — never a schema bound — is what refuses a larger plan.
    /// </summary>
    public const int MaxManifestExecutionsCeiling = 1_000_000;

    /// <summary>Published default: the most report entries one raw report may declare.</summary>
    public const int DefaultMaxReportCases = 150_000;

    /// <summary>Published default: the most variants per test case a manifest's matrix may declare.</summary>
    public const int DefaultMaxManifestVariants = 20;

    /// <summary>The ceiling a deployment may raise <see cref="MaxManifestVariants"/> to; the manifest schema admits it.</summary>
    public const int MaxManifestVariantsCeiling = 1000;

    /// <summary>
    /// Manifest body cap in bytes, counted as the body arrives and refused once it passes this value — the
    /// uncompressed size, so a manifest carrying optional gzip is bounded before the decode.
    /// </summary>
    [Range(1, 67_108_864, ErrorMessage = "EVIDGATE__INGESTION__MAXMANIFESTBYTES must be between 1 and 67108864")]
    public int MaxManifestBytes { get; init; } = DefaultMaxManifestBytes;

    /// <summary>
    /// The most planned executions a manifest may declare. Checked before the executions are expanded into
    /// rows, so an over-cap manifest costs one count and creates nothing.
    /// </summary>
    [Range(1, 1_000_000, ErrorMessage = "EVIDGATE__INGESTION__MAXMANIFESTEXECUTIONS must be between 1 and 1000000")]
    public int MaxManifestExecutions { get; init; } = DefaultMaxManifestExecutions;

    /// <summary>The most report entries (JUnit <c>testcase</c> elements) one raw report may carry.</summary>
    [Range(1, 1_000_000, ErrorMessage = "EVIDGATE__INGESTION__MAXREPORTCASES must be between 1 and 1000000")]
    public int MaxReportCases { get; init; } = DefaultMaxReportCases;

    /// <summary>
    /// The most variants one case may carry, which is the manifest matrix's variant axis: a plan over
    /// <c>cases × variants × environments</c> multiplies every extra variant across every case.
    /// </summary>
    [Range(1, 1_000, ErrorMessage = "EVIDGATE__INGESTION__MAXMANIFESTVARIANTS must be between 1 and 1000")]
    public int MaxManifestVariants { get; init; } = DefaultMaxManifestVariants;

    /// <summary>
    /// Hard cap on the bytes a gzip body may expand to (default 8 MiB, DECISIONS §8.1 D-A8). The
    /// decode aborts the moment the expanded size passes it, before the JSON parser ever sees the body.
    /// </summary>
    [Range(1_048_576, 67_108_864, ErrorMessage = "EVIDGATE__INGESTION__MAXEXPANDEDBYTES must be between 1048576 and 67108864")]
    public int MaxExpandedBytes { get; init; } = 8_388_608;

    /// <summary>
    /// Maximum expansion ratio (default 20:1, DECISIONS §8.1 D-A8): a gzip body whose decoded size
    /// passes <c>ratio × compressed size</c> is refused even when it is still under
    /// <see cref="MaxExpandedBytes"/>, so a tiny compressed body cannot buy a large parse budget.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "EVIDGATE__INGESTION__MAXEXPANSIONRATIO must be between 1 and 1000")]
    public int MaxExpansionRatio { get; init; } = 20;
}

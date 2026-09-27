namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// One entry of a <see cref="ContractValidationResult"/> — the 00 §9.2
/// <c>details[]</c> row shape the CLI's Ajv suite must reproduce exactly
/// (01.3.2 AC3/T42): a 0-based batch item <see cref="Index"/> when the document
/// is a batch (00 §9.4 <c>events[]</c>), an absolute JSON pointer
/// <see cref="Path"/> (<c>""</c> names the document itself), and a
/// machine-readable lower_snake <see cref="Reason"/> derived from the schema
/// keyword that failed.
/// </summary>
/// <param name="Index">Batch item index, or <c>null</c> for document-level errors.</param>
/// <param name="Path">Absolute JSON pointer to the failing member; <c>""</c> for the document root.</param>
/// <param name="Reason">
/// The failed constraint as a lower_snake token (<c>required</c>, <c>format</c>,
/// <c>max_items</c>, <c>body_too_large</c>, <c>invalid_json</c>…). Engine
/// diagnostic text is never surfaced: the token is derived from the schema
/// keyword so both runtimes produce the same string.
/// </param>
public sealed record ContractValidationError(int? Index, string Path, string Reason)
{
    /// <summary>Reason for a body that exceeds the schema's <c>x-max-bytes</c> transport cap (00 §9.3).</summary>
    public const string BodyTooLargeReason = "body_too_large";

    /// <summary>Reason for a body that is not JSON at all.</summary>
    public const string InvalidJsonReason = "invalid_json";

    /// <summary>
    /// Reason of the terminal marker row both runtimes emit when the
    /// <see cref="ContractValidationResult.MaxDetails"/> cap truncates the mapped
    /// errors (fixtures/contracts/README.md).
    /// </summary>
    public const string TruncatedReason = "truncated";


    /// <summary>The whole document as a single <c>body_too_large</c> detail.</summary>
    public static ContractValidationError BodyTooLarge { get; } = new(null, string.Empty, BodyTooLargeReason);

    /// <summary>The whole document as a single <c>invalid_json</c> detail.</summary>
    public static ContractValidationError InvalidJson { get; } = new(null, string.Empty, InvalidJsonReason);

    /// <summary>The terminal marker row both runtimes append as the last detail when the <see cref="ContractValidationResult.MaxDetails"/> cap fires (fixtures/contracts/README.md).</summary>
    public static ContractValidationError Truncated { get; } = new(null, string.Empty, TruncatedReason);
}

/// <summary>
/// The outcome of validating one JSON document against a contract schema:
/// <see cref="Errors"/> is empty iff the document is accepted. Entries are in
/// the order the validator produced them (envelope-level errors first, then
/// batch items in document order); the parity suites compare them as sets.
/// </summary>
/// <param name="Errors">Every mapped error; empty means the document is valid.</param>
public sealed record ContractValidationResult(IReadOnlyList<ContractValidationError> Errors)
{
    /// <summary>
    /// The hard cap on collected detail rows: at most this many mapped errors are
    /// kept; when a further row would exceed it both runtimes stop collecting and
    /// emit <see cref="ContractValidationError.Truncated"/> as the last element.
    /// The CLI applies the identical rule (fixtures/contracts/README.md).
    /// </summary>
    public const int MaxDetails = 100;

    /// <summary>The shared empty result.</summary>
    public static ContractValidationResult Valid { get; } = new([]);

    /// <summary>True when the document validates.</summary>
    public bool IsValid => Errors.Count == 0;
}

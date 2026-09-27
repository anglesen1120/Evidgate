using System.ComponentModel.DataAnnotations;

namespace Evidgate.BuildingBlocks.Persistence.Isolation;

/// <summary>How the process treats the boot-time isolation self-check (02.6.1-T1.4, AC5).</summary>
public enum IsolationSelfCheckMode
{
    /// <summary>The default: refuse to start unless every non-exempt table is under database-enforced isolation.</summary>
    Required,

    /// <summary>
    /// Explicit opt-out for a process that deliberately boots without a database
    /// (contract-surface and shutdown test hosts). It is logged at Warning on every
    /// boot, so a deployment cannot disable isolation checking quietly.
    /// </summary>
    Disabled,
}

/// <summary>
/// The isolation self-check switch, from <c>EVIDGATE__ISOLATION__SELFCHECK</c>
/// (02.6.1-T1.4). The default is <see cref="IsolationSelfCheckMode.Required"/>:
/// fail-closed, because a process that cannot confirm tenant isolation must not
/// serve tenant traffic. <see cref="IsolationSelfCheckMode.Disabled"/> exists only
/// for hosts that intentionally run without a database (the contract and shutdown
/// suites); it is named <c>Disabled</c> rather than a boolean so a deployment that
/// never sets it cannot be mistaken for one that turned isolation off, and it is
/// announced at Warning on every boot.
/// </summary>
public sealed class IsolationSelfCheckOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__ISOLATION__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Isolation";

    /// <summary>Whether the boot must confirm database-enforced isolation (default <c>Required</c>).</summary>
    [EnumDataType(
        typeof(IsolationSelfCheckMode),
        ErrorMessage = "EVIDGATE__ISOLATION__SELFCHECK must be Required or Disabled")]
    public IsolationSelfCheckMode SelfCheck { get; init; } = IsolationSelfCheckMode.Required;
}

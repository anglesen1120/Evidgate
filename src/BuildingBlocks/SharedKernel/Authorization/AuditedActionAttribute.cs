using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.SharedKernel.Authorization;

/// <summary>
/// Marks a command handler whose successful run appends exactly one <c>audit_log</c> row in the
/// same transaction (02.7.1-T2.1, AC1). The action is a lower-snake dotted string such as
/// <c>membership.invite</c>; the handler adds object detail through <c>IAuditTrail.Describe</c>.
/// An architecture test requires the attribute on every mutating handler of a real module, so a
/// new mutation cannot ship unaudited.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AuditedActionAttribute : Attribute
{
    private static readonly Regex Shape = new("^[a-z_]+(\\.[a-z_]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the marker.</summary>
    /// <param name="action">The audit action, e.g. <c>project.create</c>.</param>
    public AuditedActionAttribute(string action)
    {
        if (action is null || !Shape.IsMatch(action))
        {
            throw new ArgumentException(
                $"Invalid audit action '{action}'; expected lower-snake dotted words such as \"membership.invite\".",
                nameof(action));
        }

        Action = action;
    }

    /// <summary>The audit action recorded on success.</summary>
    public string Action { get; }
}

using System.Globalization;

namespace Evidgate.Testing;

/// <summary>
/// Random identifiers with the shape the schema accepts, for seeds that carry a unique
/// constraint (slugs, addresses, provider subjects). Tests sharing one database must not
/// use fixed literals: two suites would collide on <c>organization.slug</c> or
/// <c>user_identity (provider, subject)</c>.
/// </summary>
public static class Unique
{
    /// <summary>A slug matching the migrations' grammar, unique per call.</summary>
    /// <param name="prefix">The readable part in front of the suffix.</param>
    public static string Slug(string prefix) => Suffixed(prefix);

    /// <summary>A lower-case address, unique per call.</summary>
    /// <param name="prefix">The local-part prefix.</param>
    public static string Email(string prefix = "user") =>
        string.Create(CultureInfo.InvariantCulture, $"{Suffixed(prefix)}@example.com");

    /// <summary>A free-text identifier (provider subject, label, name), unique per call.</summary>
    /// <param name="prefix">The readable part in front of the suffix.</param>
    public static string Text(string prefix) => Suffixed(prefix);

    private static string Suffixed(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}");
    }
}

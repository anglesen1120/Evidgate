namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The shapes PostgreSQL <c>text</c> can hold: no U+0000 (the database cannot store it in a text value at all) and no
/// UTF-16 surrogate that is not the first half of a real pair (it has no UTF-8 encoding to write). A value outside these
/// shapes would be a server error, so every text a module writes is checked here first.
/// </summary>
internal static class PostgresText
{
    /// <summary>Whether <paramref name="value"/> is text PostgreSQL can store.</summary>
    /// <param name="value">The candidate text; never null at the call sites, which have already checked for null.</param>
    internal static bool IsStorable(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (character == '\0' || char.IsLowSurrogate(character))
            {
                return false;
            }

            if (!char.IsHighSurrogate(character))
            {
                continue;
            }

            // A high surrogate is storable only as the first half of a pair.
            if (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }
}

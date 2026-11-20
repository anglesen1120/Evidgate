using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Orchestrator.Application;

/// <summary>
/// The <c>details[].path</c> of a request member the route does not accept. Echoing the caller's name answers "which
/// member?", but a name holding a pointer separator, an invisible character, an unpaired surrogate or unbounded length
/// would steer or bloat the refusal, so those answer the placeholder instead.
/// </summary>
internal static class RequestMemberPath
{
    /// <summary>The stable path an unreadable, over-long or unsafe member name is reported under.</summary>
    internal const string InvalidMemberName = "/<invalid-member-name>";

    /// <summary>Longer names answer the placeholder, so none is cut inside a surrogate pair.</summary>
    internal const int MaxEchoedNameLength = 64;

    /// <summary>The path of <paramref name="name"/> below <paramref name="prefix"/>, or the placeholder when it cannot be echoed.</summary>
    internal static string ForUnknown(string? name, string prefix = "")
    {
        if (name is null
            || name.Length > MaxEchoedNameLength
            || name.Contains('/')
            || name.Contains('~')
            || !UnicodeCleanliness.IsClean(name))
        {
            return InvalidMemberName;
        }

        return prefix + "/" + name;
    }

    /// <summary>
    /// The path of the first member outside <paramref name="allowed"/>, or null when all of them are allowed. A name
    /// the reader cannot decode (a lone-surrogate escape throws while enumerated) answers the placeholder.
    /// </summary>
    internal static string? FirstDisallowed(JsonElement root, ReadOnlySpan<string> allowed)
    {
        try
        {
            foreach (var member in root.EnumerateObject())
            {
                if (!allowed.Contains(member.Name))
                {
                    return ForUnknown(member.Name);
                }
            }

            return null;
        }
        catch (InvalidOperationException)
        {
            return InvalidMemberName;
        }
    }
}

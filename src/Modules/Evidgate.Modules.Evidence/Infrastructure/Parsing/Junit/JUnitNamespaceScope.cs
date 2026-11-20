using System.Xml;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// The namespace scope of one record stream: what each open element declared, and the small identity every
/// declaration is compared by.
/// <para>
/// A URI can be megabytes, so it is hashed once when its declaration is read and never again per attribute; the
/// duplicate-attribute check compares ids. Two prefixes bound to one URI still share an identity, which is what a
/// name-aware reader resolved them to. The id map is bounded by the rebuild's byte budget, which every declaration
/// is charged to.
/// </para>
/// </summary>
internal sealed class JUnitNamespaceScope
{
    private const string XmlNamespaceUri = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespaceUri = "http://www.w3.org/2000/xmlns/";
    private const string XmlPrefix = "xml";
    private const string XmlnsPrefix = "xmlns";
    private const string XmlSpaceAttribute = "space";

    private const int NoNamespaceId = 0;
    private const int XmlNamespaceId = 1;

    private readonly List<Dictionary<string, int>?> declared = [];
    private readonly Dictionary<string, int> namespaceIds = new(StringComparer.Ordinal);

    private int nextNamespaceId = XmlNamespaceId;

    /// <summary>Opens one element's scope by reading the namespace declarations it carries.</summary>
    /// <param name="attributes">The start record's attribute pairs, or <see langword="null"/> when it has none.</param>
    public void Push(List<(string Key, string Value)>? attributes) =>
        declared.Add(Declare(attributes));

    /// <summary>Closes the innermost element's scope.</summary>
    public void Pop() => declared.RemoveAt(declared.Count - 1);

    /// <summary>Refuses an element name whose prefix no open scope declares, or a reserved one.</summary>
    /// <param name="prefix">The element name's prefix, or <see langword="null"/> for an unprefixed name.</param>
    public void VerifyElementPrefix(string? prefix)
    {
        if (prefix is null)
        {
            return;
        }

        if (string.Equals(prefix, XmlnsPrefix, StringComparison.Ordinal))
        {
            throw new XmlException("the xmlns prefix may not name an element");
        }

        if (string.Equals(prefix, XmlPrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (Lookup(prefix) is null)
        {
            throw new XmlException("an element names a prefix that is not declared");
        }
    }

    /// <summary>Refuses an attribute list a reader refused: an undeclared prefix, a bad character or a duplicate.</summary>
    /// <param name="attributes">The start record's attribute pairs, or <see langword="null"/> when it has none.</param>
    public void VerifyAttributes(List<(string Key, string Value)>? attributes)
    {
        if (attributes is null)
        {
            return;
        }

        // A duplicate is the same namespace and local part, so two prefixes for one namespace are a duplicate too.
        // The namespace side is an id, so the set never rehashes a declared URI.
        HashSet<(int Namespace, string Local)>? seen = attributes.Count > 1 ? [] : null;
        foreach (var (key, value) in attributes)
        {
            var (prefix, local) = SplitName(key);
            if (string.Equals(prefix, XmlnsPrefix, StringComparison.Ordinal)
                || (prefix is null && string.Equals(local, XmlnsPrefix, StringComparison.Ordinal)))
            {
                // A namespace declaration was validated with the element's scope; its own namespace is not the
                // attribute namespace below, so it takes no part in the duplicate set either.
                continue;
            }

            XmlConvert.VerifyXmlChars(value);

            var attributeNamespace = prefix is null
                ? NoNamespaceId
                : string.Equals(prefix, XmlPrefix, StringComparison.Ordinal)
                    ? XmlNamespaceId
                    : Lookup(prefix) ?? throw new XmlException("an attribute names a prefix that is not declared");

            if (attributeNamespace == XmlNamespaceId && string.Equals(local, XmlSpaceAttribute, StringComparison.Ordinal))
            {
                CheckXmlSpace(value);
            }

            if (seen is not null && !seen.Add((attributeNamespace, local)))
            {
                throw new XmlException("an element carries one attribute twice");
            }
        }
    }

    private Dictionary<string, int>? Declare(List<(string Key, string Value)>? attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        Dictionary<string, int>? declarations = null;
        foreach (var (key, value) in attributes)
        {
            var (prefix, local) = SplitName(key);
            if (prefix is null)
            {
                if (!string.Equals(key, XmlnsPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else if (!string.Equals(prefix, XmlnsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var declaredPrefix = prefix is null ? string.Empty : local;
            CheckDeclaration(declaredPrefix, value);

            declarations ??= new Dictionary<string, int>(StringComparer.Ordinal);
            if (!declarations.TryAdd(declaredPrefix, NamespaceId(value)))
            {
                throw new XmlException("an element declares one prefix twice");
            }
        }

        return declarations;
    }

    private int NamespaceId(string uri)
    {
        if (uri.Length == 0)
        {
            return NoNamespaceId;
        }

        if (string.Equals(uri, XmlNamespaceUri, StringComparison.Ordinal))
        {
            return XmlNamespaceId;
        }

        if (namespaceIds.TryGetValue(uri, out var id))
        {
            return id;
        }

        id = ++nextNamespaceId;
        namespaceIds.Add(uri, id);

        return id;
    }

    private static void CheckDeclaration(string prefix, string value)
    {
        XmlConvert.VerifyXmlChars(value);

        if (string.Equals(prefix, XmlnsPrefix, StringComparison.Ordinal))
        {
            throw new XmlException("the xmlns prefix may not be declared");
        }

        if (string.Equals(value, XmlnsNamespaceUri, StringComparison.Ordinal))
        {
            throw new XmlException("no prefix may be bound to the xmlns namespace");
        }

        if (string.Equals(prefix, XmlPrefix, StringComparison.Ordinal))
        {
            if (!string.Equals(value, XmlNamespaceUri, StringComparison.Ordinal))
            {
                throw new XmlException("the xml prefix may only be bound to its own namespace");
            }

            return;
        }

        if (value.Length == 0)
        {
            if (prefix.Length != 0)
            {
                throw new XmlException("a prefix may not be undeclared");
            }

            return;
        }

        if (string.Equals(value, XmlNamespaceUri, StringComparison.Ordinal))
        {
            throw new XmlException("the xml namespace may not be bound to another prefix");
        }
    }

    /// <summary>
    /// Refuses the <c>xml:space</c> value a reader refused: with XML whitespace trimmed it must be exactly
    /// <c>default</c> or <c>preserve</c>. Any other whitespace is content, so it stays a refusal rather than padding.
    /// </summary>
    /// <param name="value">The reserved attribute's value.</param>
    private static void CheckXmlSpace(string value)
    {
        var trimmed = value.AsSpan().Trim(" \t\r\n");
        if (!trimmed.SequenceEqual("default") && !trimmed.SequenceEqual("preserve"))
        {
            throw new XmlException("xml:space carries neither default nor preserve");
        }
    }

    /// <summary>Splits a qualified name the way a name-aware reader did, refusing a name it could not have read.</summary>
    internal static (string? Prefix, string Local) SplitName(string name)
    {
        var colon = name.IndexOf(':');
        if (colon < 0)
        {
            XmlConvert.VerifyNCName(name);

            return (null, name);
        }

        if (colon == 0 || colon == name.Length - 1 || name.LastIndexOf(':') != colon)
        {
            throw new XmlException("a name is not one prefix and one local part");
        }

        var prefix = name[..colon];
        var local = name[(colon + 1)..];

        // NCName refuses a colon in either part, so a name can never carry a second one.
        XmlConvert.VerifyNCName(prefix);
        XmlConvert.VerifyNCName(local);

        return (prefix, local);
    }

    private int? Lookup(string prefix)
    {
        for (var index = declared.Count - 1; index >= 0; index--)
        {
            if (declared[index] is { } bindings && bindings.TryGetValue(prefix, out var id))
            {
                return id;
            }
        }

        return null;
    }
}

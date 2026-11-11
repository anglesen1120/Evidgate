using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The Data Protection key ring in PostgreSQL (<c>public.data_protection_key</c>), so every replica shares the
/// keys that protect the login, link and antiforgery cookies and a restart keeps them.
/// </summary>
/// <remarks>
/// <para>
/// The table is reached only through <c>evidgate_resolve.data_protection_key_list</c> and
/// <c>data_protection_key_store</c> (the app role holds no privilege on it). The framework's key manager
/// calls this repository synchronously, so the statements are synchronous too; they run once per ring
/// refresh (every 24 h by default) and at key creation, never per request.
/// </para>
/// <para>
/// There is no fallback: a database failure propagates, and the key manager then fails the protect or
/// unprotect call instead of minting an ephemeral ring. Keys are stored as the framework writes them; at-rest
/// protection is the database's storage encryption (00 §5.7 S8), and the table is secret-grade for backups and
/// access (docs/runbooks/key-rotation.md).
/// </para>
/// </remarks>
internal sealed class DataProtectionKeyRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var command = dataSource.CreateCommand(
            "select key_xml from evidgate_resolve.data_protection_key_list()");
        using var reader = command.ExecuteReader();

        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(friendlyName);

        using var command = dataSource.CreateCommand(
            "select evidgate_resolve.data_protection_key_store(@name, @xml)");
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Identity.IntegrationTests;

/// <summary>One <c>permissions[]</c> entry of <c>contracts/permissions.json</c>.</summary>
public sealed record CatalogueEntry(
    string Code,
    string Resource,
    string Action,
    string Description,
    bool Sensitive,
    bool Reserved,
    bool TenantAssignable);

/// <summary>One <c>roles[]</c> entry of <c>contracts/permissions.json</c>.</summary>
public sealed record CatalogueRole(
    string Code,
    string Name,
    string Kind,
    string Description,
    IReadOnlyList<string> Permissions);

/// <summary>The whole catalogue document.</summary>
public sealed record CatalogueDocument(
    int Version,
    string Comment,
    IReadOnlyList<CatalogueRole> Roles,
    IReadOnlyList<CatalogueEntry> Permissions);

/// <summary>
/// The catalogue file as the tests read it (02.2.1-T1, AC1/AC2/AC5): the repository's
/// <c>contracts/permissions.json</c>, parsed with snake_case names, plus the derived
/// views the assertions use (the expected <c>(role, permission)</c> pairs, the holder
/// sets per code). Read from the repository, never copied into the test output, so a
/// test can never pass against a stale copy of the file the migration seeds from.
/// </summary>
public sealed class PermissionCatalogueFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    // xUnit builds one instance per test and every catalogue suite reads the same repository
    // file, so it is parsed once per process. Still the repository's own file, read at first
    // use — never a copy in the test output.
    private static readonly Lazy<PermissionCatalogueFile> Shared = new(() =>
    {
        var path = Path.Combine(RepoRoot(), "contracts", "permissions.json");
        return new PermissionCatalogueFile(
            JsonSerializer.Deserialize<CatalogueDocument>(File.ReadAllText(path), Options)!);
    });

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _holders;

    private PermissionCatalogueFile(CatalogueDocument document)
    {
        Document = document;
        Codes = [.. document.Permissions.Select(permission => permission.Code).Order(StringComparer.Ordinal)];
        _holders = document.Roles.ToDictionary(
            role => role.Code,
            role => (IReadOnlyList<string>)[.. role.Permissions.Order(StringComparer.Ordinal)],
            StringComparer.Ordinal);
    }

    /// <summary>The parsed document.</summary>
    internal CatalogueDocument Document { get; }

    /// <summary>Every declared permission code, ordinal order.</summary>
    internal IReadOnlyList<string> Codes { get; }

    /// <summary>Loads the repository's catalogue.</summary>
    internal static PermissionCatalogueFile Load() => Shared.Value;

    /// <summary>The permission codes of one role, ordinal order.</summary>
    internal IReadOnlyList<string> PermissionsOf(string roleCode) => _holders[roleCode];

    /// <summary>Every role code, ordinal order.</summary>
    internal IReadOnlyList<string> RoleCodes =>
        [.. _holders.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The role codes holding <paramref name="permissionCode"/>, ordinal order.</summary>
    internal IReadOnlyList<string> HoldersOf(string permissionCode) =>
        [.. _holders.Where(pair => pair.Value.Contains(permissionCode, StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)];

    /// <summary>The expected <c>(role, permission)</c> seed pairs, ordinal order.</summary>
    internal IReadOnlyList<(string Role, string Permission)> ExpectedGrants() =>
        [.. _holders.SelectMany(pair => pair.Value.Select(permission => (Role: pair.Key, Permission: permission)))
            .OrderBy(pair => pair.Role, StringComparer.Ordinal)
            .ThenBy(pair => pair.Permission, StringComparer.Ordinal)];

    /// <summary>The repository root, found by walking up from the test output directory.</summary>
    internal static string RepoRoot()
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

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}

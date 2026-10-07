using System.Text.Json;
using Evidgate.Testing;

namespace Identity.IntegrationTests;

/// <summary>
/// One grant row of the fixture: a permission code, the plan citation it came from, and
/// its provenance <c>basis</c> (<c>explicit</c> when the cited text affirms the grant,
/// <c>inferred</c> when the holder is derived; absent means <c>explicit</c>).
/// </summary>
internal sealed record RoleMatrixGrant(string Permission, string Source, string? Basis);

/// <summary>One role of the fixture: its code, its kind and its grant rows.</summary>
internal sealed record RoleMatrixRole(string Code, string Kind, IReadOnlyList<RoleMatrixGrant> Grants);

/// <summary>The fixture document (02.2.1-T3.1).</summary>
internal sealed record RoleMatrixDocument(
    string Comment,
    IReadOnlyList<RoleMatrixRole> Roles,
    IReadOnlyList<string> Permissions);

/// <summary>
/// The 00 §2 role × permission matrix as the <b>plans</b> state it (02.2.1-T3.1, AC1):
/// the expectation the generated matrix test compares the seeded catalogue against, on
/// both the database rows and the real evaluator. It is deliberately not read from
/// <c>contracts/permissions.json</c> — that file is the seed's source, so deriving the
/// expectation from it would make the test tautological; the fixture is transcribed from
/// the plan text and every grant carries its plan citation.
/// </summary>
/// <remarks>
/// <para>
/// Loaded through <see cref="FixtureLoader"/>, so the 01.6.1-T3.1 corpus shape contract
/// applies (exactly one <c>input.*</c>, the four-field <c>meta.json</c>, UTF-8 without
/// BOM). The document is then validated: every role's kind is one of
/// <c>member|service|staff</c>, no role or grant is duplicated, and every grant names a
/// code in the fixture's own <c>permissions</c> axis — a malformed fixture fails loudly
/// instead of silently shrinking the matrix. A grant's <c>basis</c> is <c>explicit</c>
/// (default) or <c>inferred</c>; anything else is rejected, and the inferred rows are
/// exposed through <see cref="InferredGrants"/> so the plan text that does not pin a
/// holder can be strengthened without touching the matrix.
/// </para>
/// <para>
/// Every list this class returns is ordinal-sorted, and an unknown role or permission
/// code throws rather than answering an empty set, so a typo in a test cannot pass
/// vacuously.
/// </para>
/// </remarks>
internal sealed class RoleMatrixFixture
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>The role kinds the catalogue defines (W7-SEC-03).</summary>
    private static readonly string[] Kinds = ["member", "service", "staff"];

    /// <summary>Grant provenance: the cited text affirms the grant (the default).</summary>
    private const string Explicit = "explicit";

    /// <summary>Grant provenance: the holder is derived (the plan is silent on it).</summary>
    private const string Inferred = "inferred";

    private readonly Dictionary<string, RoleMatrixRole> _roles;
    private readonly Dictionary<string, IReadOnlyList<string>> _holders;

    private RoleMatrixFixture(RoleMatrixDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var permissions = document.Permissions;
        if (permissions.Count == 0)
        {
            throw new InvalidOperationException("the fixture names no permissions");
        }

        if (permissions.Distinct(StringComparer.Ordinal).Count() != permissions.Count)
        {
            throw new InvalidOperationException("the fixture's `permissions` axis has a duplicate code");
        }

        var permissionSet = permissions.ToHashSet(StringComparer.Ordinal);
        var roles = new Dictionary<string, RoleMatrixRole>(StringComparer.Ordinal);
        var holders = permissions.ToDictionary(
            code => code,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var inferred = new List<(string Role, string Permission, string Source)>();

        foreach (var role in document.Roles)
        {
            if (!roles.TryAdd(role.Code, role))
            {
                throw new InvalidOperationException($"the fixture declares role '{role.Code}' twice");
            }

            if (!Kinds.Contains(role.Kind, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"role '{role.Code}' has kind '{role.Kind}', not one of {string.Join(", ", Kinds)}");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grant in role.Grants)
            {
                if (!permissionSet.Contains(grant.Permission))
                {
                    throw new InvalidOperationException(
                        $"role '{role.Code}' grants '{grant.Permission}', which is not in the fixture's `permissions` axis");
                }

                if (!seen.Add(grant.Permission))
                {
                    throw new InvalidOperationException(
                        $"role '{role.Code}' grants '{grant.Permission}' twice");
                }

                if (string.IsNullOrWhiteSpace(grant.Source))
                {
                    throw new InvalidOperationException(
                        $"role '{role.Code}' grant '{grant.Permission}' has no plan source");
                }

                var basis = grant.Basis ?? Explicit;
                if (basis is not (Explicit or Inferred))
                {
                    throw new InvalidOperationException(
                        $"role '{role.Code}' grant '{grant.Permission}' has basis '{basis}', not '{Explicit}' or '{Inferred}'");
                }

                if (basis == Inferred)
                {
                    inferred.Add((role.Code, grant.Permission, grant.Source));
                }

                holders[grant.Permission].Add(role.Code);
            }
        }

        if (roles.Count == 0)
        {
            throw new InvalidOperationException("the fixture declares no roles");
        }

        _roles = roles;
        _holders = holders.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)[.. pair.Value.Order(StringComparer.Ordinal)],
            StringComparer.Ordinal);

        RoleCodes = [.. roles.Keys.Order(StringComparer.Ordinal)];
        PermissionCodes = [.. permissions.Order(StringComparer.Ordinal)];
        InferredGrants =
        [
            .. inferred
                .OrderBy(grant => grant.Role, StringComparer.Ordinal)
                .ThenBy(grant => grant.Permission, StringComparer.Ordinal),
        ];
    }

    /// <summary>Every role code, ordinal order.</summary>
    public IReadOnlyList<string> RoleCodes { get; }

    /// <summary>Every permission code of the matrix's permission axis, ordinal order.</summary>
    public IReadOnlyList<string> PermissionCodes { get; }

    /// <summary>
    /// The grants whose holder the plan text does not affirm (basis <c>inferred</c>), ordinal by
    /// role then permission. The plans pin the permission but not the role, so the owner of the
    /// authoritative text is named by <c>Source</c>; the matrix itself is unaffected.
    /// </summary>
    public IReadOnlyList<(string Role, string Permission, string Source)> InferredGrants { get; }

    /// <summary>Loads the repository's role × permission matrix fixture.</summary>
    public static RoleMatrixFixture Load()
    {
        var fixture = FixtureLoader.Load(FixtureLoader.Fixtures("identity/role-permission-matrix"));
        var document = JsonSerializer.Deserialize<RoleMatrixDocument>(fixture.InputText, Options)
            ?? throw new InvalidOperationException("the role-permission-matrix fixture is not a JSON object");

        return new RoleMatrixFixture(document);
    }

    /// <summary>The permission codes of one role, ordinal order.</summary>
    /// <exception cref="KeyNotFoundException">the fixture declares no such role.</exception>
    public IReadOnlyList<string> PermissionsOf(string role)
    {
        if (!_roles.TryGetValue(role, out var entry))
        {
            throw new KeyNotFoundException($"no role '{role}' in the role-permission-matrix fixture");
        }

        return [.. entry.Grants.Select(grant => grant.Permission).Order(StringComparer.Ordinal)];
    }

    /// <summary>The role codes holding one permission, ordinal order.</summary>
    /// <exception cref="KeyNotFoundException">the fixture declares no such permission.</exception>
    public IReadOnlyList<string> HoldersOf(string code) =>
        _holders.TryGetValue(code, out var holders)
            ? holders
            : throw new KeyNotFoundException($"no permission '{code}' in the role-permission-matrix fixture");

    /// <summary>True when the fixture grants <paramref name="code"/> to <paramref name="role"/>.</summary>
    /// <exception cref="KeyNotFoundException">the fixture declares no such role or permission.</exception>
    public bool Holds(string role, string code)
    {
        if (!_roles.TryGetValue(role, out var entry))
        {
            throw new KeyNotFoundException($"no role '{role}' in the role-permission-matrix fixture");
        }

        if (!_holders.ContainsKey(code))
        {
            throw new KeyNotFoundException($"no permission '{code}' in the role-permission-matrix fixture");
        }

        return entry.Grants.Any(grant => string.Equals(grant.Permission, code, StringComparison.Ordinal));
    }

    /// <summary>The kind of one role (<c>member|service|staff</c>).</summary>
    /// <exception cref="KeyNotFoundException">the fixture declares no such role.</exception>
    public string KindOf(string role) =>
        _roles.TryGetValue(role, out var entry)
            ? entry.Kind
            : throw new KeyNotFoundException($"no role '{role}' in the role-permission-matrix fixture");
}

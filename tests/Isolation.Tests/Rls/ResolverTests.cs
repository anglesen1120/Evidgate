using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 02.9.1-T3.2 (story 02.9.1 §8 T1, §9 "Resolver surface"; 00 §5.4.8, TL T1/T80):
/// the pre-tenant resolver surface. A request authenticates before any tenant exists, so the
/// resolvers of <c>evidgate_resolve</c> are the one sanctioned way across the tenant boundary —
/// and they must be exactly that: owner-owned, <c>SECURITY DEFINER</c>, pinned to an empty
/// <c>search_path</c>, executable by <c>evidgate_app</c> and nobody else, returning only the
/// documented tuple, while the tables they read stay org-scoped for a direct read.
/// <para>
/// The function set is read from <c>pg_proc</c>, so a resolver added by a later migration is
/// covered the moment it lands; the documented tuple per function is the contract of 00 §5.4.8
/// ("return only <c>(org_id, project_id, binding_id, status)</c>"), and a resolver that is not
/// registered here fails the suite by name.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ResolverTests : HarnessTest
{
    /// <summary>
    /// The documented result tuple of every resolver, keyed by <c>schema.name</c>. A resolver is
    /// a <c>SECURITY DEFINER</c> function with an <c>org_id</c> OUT column; the tuple is the only
    /// thing it may return (00 §5.4.8, TL T1), never a column of the table it reads.
    /// </summary>
    private static readonly Dictionary<string, string[]> DocumentedResolvers = new(StringComparer.Ordinal)
    {
        ["evidgate_resolve.member_org"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.member_orgs"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.member_organizations"] = ["org_id", "slug", "name", "status"],
        ["evidgate_resolve.resolve_ci_binding"] = ["org_id", "project_id", "binding_id", "status"],
        ["evidgate_resolve.resolve_ingest_token"] = ["org_id", "project_id", "binding_id", "status"],
    };

    /// <summary>
    /// The tables the resolvers read (0014: <c>membership</c>/<c>organization</c>; 0016:
    /// <c>ci_binding</c>; 0018: <c>ingest_token</c>), with the policy predicate that keeps a
    /// direct read inside one org.
    /// </summary>
    private static readonly (string Table, string OrgPredicate)[] ResolvedTables =
    [
        ("public.membership", RlsTableCatalog.TenantPredicate),
        ("public.organization", RlsTableCatalog.OrganizationPredicate),
        ("public.ci_binding", RlsTableCatalog.TenantPredicate),
        ("public.ingest_token", RlsTableCatalog.TenantPredicate),
    ];

    /// <summary>
    /// The platform identity tables of AC5 (D12): they hold no customer business data and are
    /// reached only through a membership, never by a direct tenant read. The contract is
    /// therefore stronger than the resolved tables' — the app role must hold no direct SELECT
    /// on them at all, FORCE row level security or not (02.9.1 §8 T1). The exception list
    /// allows the entry before the migration that creates the table, so an absent one is reported
    /// as inert rather than skipped.
    /// </summary>
    private static readonly string[] PlatformIdentityTables = ["public.user", "public.user_identity", "public.session"];

    private readonly ITestOutputHelper _output;

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public ResolverTests(DatabaseFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    /// <summary>
    /// AC5 / §8 T1: every resolver is owner-owned, <c>SECURITY DEFINER</c>, <c>search_path</c>-pinned,
    /// PUBLIC-closed and app-executable, returns exactly its documented tuple, and the tables it
    /// reads are org-scoped for a direct read — so the resolver is the only path that crosses the
    /// tenant boundary.
    /// </summary>
    [Fact]
    public async Task AppRole_ExecuteOnly_NoDirectReads()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        var functions = await RlsTableCatalog.ReadResolversAsync(connection, Token);
        functions.ShouldNotBeEmpty(
            "no SECURITY DEFINER function was found in evidgate_resolve/evidgate/public: the resolver surface is empty");

        var failures = new List<string>();

        // 1. Hygiene of every SECURITY DEFINER function (TL T80). The sweep functions of
        //    evidgate (purge_expired_*, audit_org_ids) are in this set too: they are owner-owned
        //    definers as well, and PUBLIC must not be able to call any of them.
        foreach (var function in functions)
        {
            if (!string.Equals(function.Owner, "evidgate_owner", StringComparison.Ordinal))
            {
                failures.Add($"{function.Signature}: owner is {function.Owner}, expected evidgate_owner");
            }

            if (!function.EmptySearchPath)
            {
                failures.Add(
                    $"{function.Signature}: search_path is not pinned to '' (proconfig = '{function.Configuration}') — "
                    + "a definer function must not resolve an object the caller could shadow (TL T80)");
            }

            if (function.PublicCanExecute)
            {
                failures.Add($"{function.Signature}: PUBLIC still has EXECUTE — every definer function is owner-only plus explicit grants");
            }
        }

        // 2. The resolver surface: exactly the documented set, each in evidgate_resolve, each
        //    executable by the app role, each returning only its documented tuple.
        var resolvers = functions.Where(function => function.IsResolver).ToList();
        resolvers.ShouldNotBeEmpty(
            "no resolver (a SECURITY DEFINER function with an org_id OUT column) was found: the pre-tenant path would have no sanctioned way to resolve a tenant");

        var discovered = resolvers.Select(resolver => $"{resolver.Schema}.{resolver.Name}").ToArray();
        var documented = DocumentedResolvers.Keys.ToArray();
        var missing = documented.Except(discovered, StringComparer.Ordinal).ToArray();
        var unregistered = discovered.Except(documented, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unregistered.Length > 0)
        {
            failures.Add(
                $"the resolver set does not match the documented one — missing [{string.Join(", ", missing)}], "
                + $"unregistered [{string.Join(", ", unregistered)}]: register a new resolver with its documented tuple");
        }

        foreach (var resolver in resolvers)
        {
            var key = $"{resolver.Schema}.{resolver.Name}";

            if (!string.Equals(resolver.Schema, "evidgate_resolve", StringComparison.Ordinal))
            {
                failures.Add($"{resolver.Signature}: a resolver must live in evidgate_resolve (00 §5.4.8)");
            }

            if (!resolver.AppCanExecute)
            {
                failures.Add($"{resolver.Signature}: evidgate_app has no EXECUTE — the pre-tenant path cannot resolve a tenant");
            }

            if (resolver.ResultColumns.Count == 0
                || !string.Equals(resolver.ResultColumns[0], "org_id", StringComparison.Ordinal))
            {
                failures.Add($"{resolver.Signature}: the first result column must be org_id (the tenant tuple of 00 §5.4.8)");
            }

            if (DocumentedResolvers.TryGetValue(key, out var expected)
                && !resolver.ResultColumns.SequenceEqual(expected, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{resolver.Signature}: returns ({string.Join(", ", resolver.ResultColumns)}), expected the documented tuple "
                    + $"({string.Join(", ", expected)}) — a resolver returns the tuple, never a column of the table it reads");
            }

            _output.WriteLine(
                "{0}: owner={1} search_path='' public_execute={2} app_execute={3} returns ({4})",
                resolver.Signature,
                resolver.Owner,
                resolver.PublicCanExecute,
                resolver.AppCanExecute,
                string.Join(", ", resolver.ResultColumns));
        }

        // 3. "No direct read on the resolved tables": the app role may hold SELECT on them, but
        //    only under FORCE row level security with an org-scoped policy — otherwise the
        //    resolver would not be the only path that crosses the tenant boundary.
        var tables = await RlsTableCatalog.ReadTablesAsync(connection, Token);
        var policies = await RlsTableCatalog.ReadPoliciesAsync(connection, Token);
        var tablesByName = tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        var policiesByTable = policies
            .GroupBy(policy => policy.Table, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var (table, predicate) in ResolvedTables)
        {
            if (!tablesByName.TryGetValue(table, out var info))
            {
                failures.Add($"{table}: a resolver reads it but the table does not exist in the catalogue");
                continue;
            }

            var canRead = await RlsTableCatalog.HasTablePrivilegeAsync(connection, "evidgate_app", table, "SELECT", Token);
            if (!canRead)
            {
                _output.WriteLine("{0}: evidgate_app has no direct SELECT — reached only through the resolver", table);
                continue;
            }

            if (!info.RowLevelSecurityForced)
            {
                failures.Add(
                    $"{table}: evidgate_app holds a direct SELECT and row level security is not FORCED — a direct read could cross orgs");
            }

            var scoped = (policiesByTable.GetValueOrDefault(table) ?? [])
                .Any(policy => RlsTableCatalog.Matches(policy, predicate));
            if (!scoped)
            {
                failures.Add(
                    $"{table}: evidgate_app holds a direct SELECT and no policy matches '{predicate}' — the resolver would not be "
                    + "the only path across the tenant boundary");
            }

            _output.WriteLine(
                "{0}: evidgate_app SELECT is org-scoped (force={1}, predicate matched={2})",
                table,
                info.RowLevelSecurityForced,
                scoped);
        }

        // 4. The platform identity tables: absent ones are inert (the exception list allows the
        //    entry first); a present one the app role can read at all is a hole. These tables
        //    are reached only through evidgate_resolve and membership, so no FORCE flag and no
        //    policy makes a direct SELECT acceptable — the resolver must be the only path
        //    across the tenant boundary (00 §5.4.8, TL T1; 02.9.1 §8 T1).
        foreach (var table in PlatformIdentityTables)
        {
            if (!await RlsTableCatalog.TableExistsAsync(connection, table, Token))
            {
                _output.WriteLine("{0}: inert — the table does not exist yet", table);
                continue;
            }

            var canRead = await RlsTableCatalog.HasTablePrivilegeAsync(connection, "evidgate_app", table, "SELECT", Token);
            if (canRead)
            {
                failures.Add(
                    $"{table}: evidgate_app holds a direct SELECT on a platform identity table — it is reached only "
                    + "through evidgate_resolve and membership, so the resolver would not be the only path across the "
                    + "tenant boundary (00 §5.4.8, TL T1)");
            }

            _output.WriteLine(
                "{0}: evidgate_app SELECT={1} force={2} policies={3}",
                table,
                canRead,
                tablesByName.GetValueOrDefault(table)?.RowLevelSecurityForced ?? false,
                (policiesByTable.GetValueOrDefault(table) ?? []).Count);
        }

        failures.ShouldBeEmpty(
            $"the resolver surface is not the only path across the tenant boundary:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }
}

using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Isolation;

/// <summary>
/// The named failure codes of 02.6.1-T1.4 (AC5). They are the operator-visible
/// contract: the boot line, the exit code and the test all speak these names, and
/// none of them carries a secret.
/// </summary>
public static class IsolationSelfCheckCodes
{
    /// <summary>Boot refusal: at least one non-exempt table is not under database-enforced isolation.</summary>
    public const string SelfCheckFailed = "isolation_self_check_failed";

    /// <summary>The catalogue could not be read, so isolation could not be confirmed — fails closed.</summary>
    public const string CatalogueUnreadable = "isolation_catalogue_unreadable";

    /// <summary><c>pg_class.relrowsecurity</c> is false.</summary>
    public const string RowLevelSecurityNotEnabled = "row_level_security_not_enabled";

    /// <summary><c>pg_class.relforcerowsecurity</c> is false (the owner would bypass every policy).</summary>
    public const string RowLevelSecurityNotForced = "row_level_security_not_forced";

    /// <summary>The table has no policy at all.</summary>
    public const string NoPolicy = "no_policy";

    /// <summary>An org-carrying table has no <c>org_id = evidgate.current_org_id()</c> policy (00 §5.4.7/T2, AC1).</summary>
    public const string TenantPolicyMissing = "tenant_policy_missing";

    /// <summary>The tenant predicate exists but its policy does not apply to <c>evidgate_app</c> (<c>TO evidgate_operator</c> only, D3).</summary>
    public const string TenantPolicyWrongRole = "tenant_policy_wrong_role";

    /// <summary>No policy that applies to <c>evidgate_app</c> covers all commands, so every other command stays unscoped (`FOR SELECT` only).</summary>
    public const string TenantPolicyNotAllCommands = "tenant_policy_not_all_commands";

    /// <summary>The <c>evidgate_app</c> policy's USING does not scope rows to the current org (D5: <c>using (true)</c>).</summary>
    public const string TenantPolicyUsingNotScoped = "tenant_policy_using_not_scoped";

    /// <summary>The <c>evidgate_app</c> policy's WITH CHECK is present but does not scope rows to the current org.</summary>
    public const string TenantPolicyWithCheckNotScoped = "tenant_policy_with_check_not_scoped";

    /// <summary>A second PERMISSIVE policy that applies to <c>evidgate_app</c> is unscoped and OR-combines with the scoped one.</summary>
    public const string PermissivePolicyUnscoped = "permissive_policy_unscoped";

    /// <summary>A self-scoped platform table (today <c>public.organization</c>) has no policy carrying <c>id = evidgate.current_org_id()</c> for the runtime role (T4, D7).</summary>
    public const string PlatformPolicyMissing = "platform_policy_missing";

    /// <summary>The <c>id = …</c> predicate exists on the self-scoped platform table but on a policy the runtime role cannot use.</summary>
    public const string PlatformPolicyWrongRole = "platform_policy_wrong_role";

    /// <summary>No self-scoped platform policy that applies to <c>evidgate_app</c> covers every command.</summary>
    public const string PlatformPolicyNotAllCommands = "platform_policy_not_all_commands";

    /// <summary>The self-scoped platform table's <c>evidgate_app</c> <c>FOR ALL</c> policy's USING is not <c>id = evidgate.current_org_id()</c> (D7: <c>using (true)</c>).</summary>
    public const string PlatformPolicyUsingNotScoped = "platform_policy_using_not_scoped";

    /// <summary>Its declared WITH CHECK is present but not <c>id = evidgate.current_org_id()</c>.</summary>
    public const string PlatformPolicyWithCheckNotScoped = "platform_policy_with_check_not_scoped";

    /// <summary>A catalogue table has no all-org read policy <c>org_id IS NULL OR …</c> (02 §4.5).</summary>
    public const string CataloguePolicyMissing = "catalogue_read_policy_missing";
}

/// <summary>One table that is not under database-enforced isolation, named so a failure reads as a sentence.</summary>
/// <param name="Table">Schema-qualified table name as PostgreSQL reports it.</param>
/// <param name="Code">One of the <see cref="IsolationSelfCheckCodes"/> constants.</param>
/// <param name="Detail">Why the rule failed, in one clause.</param>
public sealed record IsolationViolation(string Table, string Code, string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{Table}: {Code} ({Detail})";
}

/// <summary>
/// The outcome of one isolation self-check (02.6.1-T1.4, AC5): what was inspected,
/// what was excused by <c>contracts/rls-exceptions.txt</c>, and every violation.
/// <see cref="FailureCode"/> is set only when the check itself could not confirm
/// isolation (the catalogue was unreadable) — which also refuses the boot, because
/// "cannot confirm" must never read as "confirmed".
/// </summary>
/// <param name="Violations">Every violated rule, in table order.</param>
/// <param name="CheckedTables">The tables the flags-and-policy rules applied to (excluding mechanism tables).</param>
/// <param name="ExemptTables">How many tables the exception list excuses entirely (kind <c>mechanism</c>).</param>
/// <param name="FailureCode">A named code when the check could not run, otherwise null.</param>
/// <param name="FailureDetail">Why the check could not run, in one clause (never a connection string).</param>
public sealed record IsolationSelfCheckReport(
    IReadOnlyList<IsolationViolation> Violations,
    IReadOnlyList<string> CheckedTables,
    int ExemptTables,
    string? FailureCode,
    string? FailureDetail)
{
    /// <summary>True only when every rule held and the check itself ran.</summary>
    public bool Passed => FailureCode is null && Violations.Count == 0;

    /// <summary>A check that could not confirm isolation (unreadable catalogue, no connection).</summary>
    public static IsolationSelfCheckReport Unreadable(string code, string detail) =>
        new([], [], 0, code, detail);
}

/// <summary>
/// The boot-time isolation self-check of 02.6.1-T1.4 (area plan 02 §7, AC5):
/// reads <c>pg_class</c> and <c>pg_policy</c> <b>as the application role</b>, once,
/// before the host serves anything, and decides whether every table that
/// <c>contracts/rls-exceptions.txt</c> does not excuse runs under
/// <c>ENABLE</c> + <c>FORCE ROW LEVEL SECURITY</c> with the policy shape its kind
/// requires (00 §5.4.3/§5.4.7; DECISIONS F5; TL T2, T3, T4).
/// <para>
/// Reading the catalogue as <c>evidgate_app</c> rather than as the owner is
/// deliberate: the check then also proves the runtime role can see the facts it is
/// judged on. The flag-and-predicate half is the rule set the CI lint of 02.6.1-T2
/// is to apply to <c>migrations/*.sql</c>; the role, command and permissiveness
/// rules below need the live catalogue and are runtime-only. Either way a policy
/// dropped on a live database after the lint passed still refuses the next boot.
/// </para>
/// <para>
/// A policy is judged through <c>pg_policy</c>'s role list, command and
/// permissiveness, not through its expression alone. A predicate on the wrong role
/// protects nobody (the runtime role then has no policy at all), a predicate on a
/// policy that does not cover every command leaves the uncovered ones unprotected,
/// and a permissive policy is OR-ed with the others — so one permissive policy that
/// applies to the runtime role without the predicate opens the table however correct
/// its siblings are. For the tenant predicate and the tenant root's self-scope
/// predicate all four facts must line up; the reader is what turns AC1's wording
/// into a refusal.
/// </para>
/// </summary>
public sealed class IsolationSelfCheck
{
    /// <summary>Seconds before an unresponsive catalogue read counts as unreadable (the boot must not hang).</summary>
    private const int CommandTimeoutSeconds = 5;

    /// <summary>
    /// The tenant predicate of the 0003 policy template, as a migration writes it
    /// (00 §5.4.7/T2). It is the display form; every comparison goes through
    /// <see cref="MatchesPredicate"/>.
    /// </summary>
    private const string TenantPredicate = "org_id = evidgate.current_org_id()";

    /// <summary>The catalogue read shape of 02 §4.5, as a migration writes it; comparisons go through <see cref="MatchesPredicate"/>.</summary>
    private const string CataloguePredicate = "org_id IS NULL OR org_id = evidgate.current_org_id()";

    /// <summary>The self-scope predicate of the tenant root (TL T4, 02 §4), as a migration writes it: `organization` has no `org_id` column, so its policy compares the row's own `id`. The display form; every comparison goes through <see cref="MatchesPredicate"/>.</summary>
    private const string OrganizationPredicate = "id = evidgate.current_org_id()";

    /// <summary>The runtime role every rule is judged for — the role both hosts connect as (00 §5.4.3).</summary>
    private const string ApplicationRole = "evidgate_app";

    /// <summary>How PostgreSQL spells the pseudo-role in a policy's role list (`polroles` 0).</summary>
    private const string PublicRole = "public";

    /// <summary>
    /// The platform-kind tables whose policy must self-scope to the current org, and the
    /// predicate it must carry: the tenant root (TL T4, 02 §4/§6) is the only one today.
    /// The rule is per-table on purpose — a platform entry excuses the table from the
    /// <c>org_id</c> rule, so nothing in the catalogue distinguishes "genuinely open
    /// platform data" (<c>public.permission</c>, read by every org) from "org-scoped data
    /// that happens to lack an <c>org_id</c> column". A new self-scoped platform table adds
    /// one line here; a new open-catalogue platform table stays out and keeps the
    /// flags-and-policy-presence rule.
    /// </summary>
    private static readonly Dictionary<string, string> PlatformSelfScopePredicates =
        new(StringComparer.Ordinal)
        {
            ["public.organization"] = OrganizationPredicate,
        };

    /// <summary>The five codes of the tenant rule (AC1): one per way the required scoped policy, or the OR-combining rule around it, can fail.</summary>
    private static readonly ScopedPolicyCodes TenantCodes = new(
        IsolationSelfCheckCodes.TenantPolicyMissing,
        IsolationSelfCheckCodes.TenantPolicyWrongRole,
        IsolationSelfCheckCodes.TenantPolicyNotAllCommands,
        IsolationSelfCheckCodes.TenantPolicyUsingNotScoped,
        IsolationSelfCheckCodes.TenantPolicyWithCheckNotScoped);

    /// <summary>The same five failure modes for the tenant root's self-scope rule (TL T4, D7).</summary>
    private static readonly ScopedPolicyCodes PlatformCodes = new(
        IsolationSelfCheckCodes.PlatformPolicyMissing,
        IsolationSelfCheckCodes.PlatformPolicyWrongRole,
        IsolationSelfCheckCodes.PlatformPolicyNotAllCommands,
        IsolationSelfCheckCodes.PlatformPolicyUsingNotScoped,
        IsolationSelfCheckCodes.PlatformPolicyWithCheckNotScoped);

    private readonly NpgsqlDataSource _dataSource;
    private readonly RlsExceptionList _exceptions;

    /// <summary>Creates the check over the app-role data source and the exception list this build embeds.</summary>
    public IsolationSelfCheck(NpgsqlDataSource dataSource, RlsExceptionList exceptions)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(exceptions);

        _dataSource = dataSource;
        _exceptions = exceptions;
    }

    /// <summary>
    /// Opens one application-role connection and inspects the migrated catalogue.
    /// A database that cannot be read is reported as <see cref="IsolationSelfCheckCodes.CatalogueUnreadable"/>
    /// rather than thrown: the caller must refuse the boot either way, and the report
    /// is what names the refusal. Never returns a passing report it did not confirm.
    /// </summary>
    public async ValueTask<IsolationSelfCheckReport> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await InspectAsync(connection, _exceptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled start is a shutdown, not a verdict: let the host unwind.
            throw;
        }
        catch (PostgresException exception)
        {
            return IsolationSelfCheckReport.Unreadable(
                IsolationSelfCheckCodes.CatalogueUnreadable,
                $"the catalogue read failed with SQLSTATE {exception.SqlState}");
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
        {
            return IsolationSelfCheckReport.Unreadable(
                IsolationSelfCheckCodes.CatalogueUnreadable,
                $"the database could not be read as the application role ({exception.GetType().Name})");
        }
    }

    /// <summary>
    /// Runs the rules over an open connection (the test seam of the 02.6.1-T1 suite:
    /// the same code decides the boot and the assertions). Reads only; the caller owns
    /// the connection.
    /// </summary>
    public static async ValueTask<IsolationSelfCheckReport> InspectAsync(
        NpgsqlConnection connection,
        RlsExceptionList exceptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(exceptions);

        var tables = await ReadTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        var policies = await ReadPoliciesAsync(connection, cancellationToken).ConfigureAwait(false);

        var violations = new List<IsolationViolation>();
        var checkedTables = new List<string>();
        var exempt = 0;

        foreach (var table in tables)
        {
            var entry = exceptions.Find(table.Name);

            // Mechanism tables are outside the rule set, not merely outside the org
            // policy: they carry org_id (nullable for `task`) and hold no policy
            // because the dispatcher scan walks every org by design (0006-0008).
            if (entry?.Kind == RlsExceptionKind.Mechanism)
            {
                exempt++;
                continue;
            }

            checkedTables.Add(table.Name);

            if (!table.RowLevelSecurityEnabled)
            {
                violations.Add(new IsolationViolation(
                    table.Name,
                    IsolationSelfCheckCodes.RowLevelSecurityNotEnabled,
                    "row level security is not enabled (DECISIONS F5)"));
            }

            if (!table.RowLevelSecurityForced)
            {
                violations.Add(new IsolationViolation(
                    table.Name,
                    IsolationSelfCheckCodes.RowLevelSecurityNotForced,
                    "row level security is not FORCED — the table owner would bypass every policy (DECISIONS F5)"));
            }

            var tablePolicies = policies.Where(policy => string.Equals(policy.Table, table.Name, StringComparison.Ordinal)).ToList();

            if (tablePolicies.Count == 0)
            {
                violations.Add(new IsolationViolation(
                    table.Name,
                    IsolationSelfCheckCodes.NoPolicy,
                    "no policy exists — every read would return zero rows or, without FORCE, every row"));
                continue;
            }

            if (entry?.Kind == RlsExceptionKind.Catalogue)
            {
                if (!tablePolicies.Any(policy => Matches(policy, CataloguePredicate)))
                {
                    violations.Add(new IsolationViolation(
                        table.Name,
                        IsolationSelfCheckCodes.CataloguePolicyMissing,
                        "no policy reads the platform catalogue rows for every org: expected "
                        + "'org_id IS NULL OR org_id = evidgate.current_org_id()' (02 §4.5)"));
                }

                // The read shape is not the whole contract: the app role also writes its own
                // rows here (the 0010 tenant INSERT/UPDATE policies), and a permissive sibling
                // is OR-ed with them, so it opens the table however correct the read shape is.
                // The tenant predicate counts as scoped too — it is strictly narrower than the
                // all-org read shape, so a policy carrying it reaches no other org's row.
                AddUnscopedPermissiveViolations(
                    table.Name,
                    ApplicablePolicies(tablePolicies),
                    CataloguePredicate,
                    TenantPredicate,
                    violations);

                continue;
            }

            // The predicate every policy of this table must carry for the runtime role:
            // the tenant predicate for an unlisted table that carries org_id (AC1), and
            // the self-scope predicate of PlatformSelfScopePredicates for a platform table
            // that is org-scoped (today the tenant root, TL T4). A platform table that is
            // neither — genuinely open platform data such as `permission` — keeps the
            // flags-and-policy-presence rule alone.
            if (entry is null)
            {
                if (table.HasOrgId)
                {
                    AddScopedPolicyViolations(table.Name, tablePolicies, TenantPredicate, TenantCodes, violations);
                }
            }
            else if (entry.Kind == RlsExceptionKind.Platform
                && PlatformSelfScopePredicates.TryGetValue(table.Name, out var platformPredicate))
            {
                AddScopedPolicyViolations(table.Name, tablePolicies, platformPredicate, PlatformCodes, violations);
            }
        }

        return new IsolationSelfCheckReport(violations, checkedTables, exempt, null, null);
    }

    /// <summary>
    /// True when the policy's USING expression is the predicate, or the predicate
    /// limited by further <c>AND</c>-ed clauses (`org_id = … and deleted_at is null`).
    /// <see cref="MatchesPredicate"/> decides that on the clause's structure: a substring
    /// or paren-stripped text match would also accept `… or true`, i.e. a policy that
    /// opens every row.
    /// </summary>
    private static bool MatchesUsing(PolicyShape policy, string predicate) =>
        policy.Using is not null && MatchesPredicate(policy.Using, predicate);

    /// <summary>
    /// True when the policy's WITH CHECK is satisfied — or, when the policy declares no
    /// WITH CHECK, when its USING is: PostgreSQL reuses USING as the WITH CHECK of a
    /// policy that declares none, so `for all … using (org_id = …)` does check the write.
    /// An INSERT-only policy can never rely on that and must declare one.
    /// </summary>
    private static bool CheckSatisfied(PolicyShape policy, string predicate) =>
        policy.WithCheck is null
            ? MatchesUsing(policy, predicate)
            : MatchesPredicate(policy.WithCheck, predicate);

    /// <summary>
    /// True when either side of the policy is the predicate. The catalogue rule of
    /// 02 §4.5 is written on either side by the migrations (a read policy carries it in
    /// USING, an insert policy in WITH CHECK), so it deliberately does not distinguish them.
    /// </summary>
    private static bool Matches(PolicyShape policy, string predicate) =>
        MatchesUsing(policy, predicate)
        || (policy.WithCheck is not null && MatchesPredicate(policy.WithCheck, predicate));

    /// <summary>
    /// Applies one scoped-policy rule set to one table: the runtime role needs a permissive
    /// <c>FOR ALL</c> policy that carries <paramref name="displayPredicate"/> in USING and in
    /// its WITH CHECK (absent is fine — PostgreSQL then reuses USING), and no other permissive
    /// policy that reaches the runtime role may be unscoped, because permissive policies are
    /// OR-ed and one of them widens the table back to every org.
    /// </summary>
    /// <param name="table">Schema-qualified table under judgement.</param>
    /// <param name="tablePolicies">Every policy of that table.</param>
    /// <param name="displayPredicate">The predicate as a migration writes it (for the message).</param>
    /// <param name="codes">The five codes this rule speaks.</param>
    /// <param name="violations">The report's violation list to append to.</param>
    private static void AddScopedPolicyViolations(
        string table,
        IReadOnlyList<PolicyShape> tablePolicies,
        string displayPredicate,
        ScopedPolicyCodes codes,
        List<IsolationViolation> violations)
    {
        // A RESTRICTIVE policy can only narrow what the permissive ones allow, so it is not
        // part of the leak surface; only permissive policies the runtime role can use are
        // judged (00 §5.4.7/T2; DECISIONS F5).
        var applicable = ApplicablePolicies(tablePolicies);

        if (applicable.Count == 0)
        {
            // Distinguish "no scoped policy at all" from "scoped policy for another role":
            // the second is the operator shape of 02.8.1/T76 — the right predicate with no
            // access for evidgate_app — and must not read as a predicate that never existed.
            var matching = tablePolicies.FirstOrDefault(policy => Matches(policy, displayPredicate));
            violations.Add(new IsolationViolation(
                table,
                matching is null ? codes.Missing : codes.WrongRole,
                matching is null
                    ? $"no policy scopes the table through '{displayPredicate}' and none applies to {ApplicationRole}"
                    : $"the policy carrying '{displayPredicate}' applies to {string.Join(", ", matching.Roles)} but not to {ApplicationRole}, so the runtime role has no scoped access"));
            return;
        }

        var allCommands = applicable.Where(policy => policy.IsAllCommands).ToList();

        var scopedAllCommands = allCommands.Any(
            policy => MatchesUsing(policy, displayPredicate) && CheckSatisfied(policy, displayPredicate));

        if (!scopedAllCommands)
        {
            // The required policy is missing: report the one rule that failed and stop — the
            // unscoped policies it names would otherwise also be listed by the OR rule below.
            if (allCommands.Count == 0)
            {
                violations.Add(new IsolationViolation(
                    table,
                    codes.NotAllCommands,
                    $"no policy that applies to {ApplicationRole} covers every command (FOR ALL), so the commands it does not name stay unscoped"));
            }
            else if (allCommands.Any(policy => MatchesUsing(policy, displayPredicate)))
            {
                violations.Add(new IsolationViolation(
                    table,
                    codes.WithCheckNotScoped,
                    $"the {ApplicationRole} FOR ALL policy declares a WITH CHECK that is not '{displayPredicate}', so a write may store another org's row"));
            }
            else
            {
                violations.Add(new IsolationViolation(
                    table,
                    codes.UsingNotScoped,
                    $"no {ApplicationRole} FOR ALL policy carries '{displayPredicate}' in USING (00 §5.4.7/T2)"));
            }

            return;
        }

        AddUnscopedPermissiveViolations(table, applicable, displayPredicate, null, violations);
    }

    /// <summary>
    /// The permissive policies of one table that reach the runtime role — the only ones whose
    /// clauses are OR-ed into what that role may read or write.
    /// </summary>
    private static List<PolicyShape> ApplicablePolicies(IReadOnlyList<PolicyShape> tablePolicies) =>
        tablePolicies
            .Where(policy => policy.Permissive && policy.AppliesTo(ApplicationRole, PublicRole))
            .ToList();

    /// <summary>
    /// The OR-combining rule shared by every scoped-policy kind: a permissive policy that
    /// reaches the runtime role without the predicate opens the table however correct its
    /// siblings are (AC1). <paramref name="narrowerPredicate"/>, when given, is a second
    /// predicate that also counts as scoped because it admits a subset of the rows the first
    /// one does — the catalogue read shape accepts the tenant predicate that way.
    /// </summary>
    /// <param name="table">Schema-qualified table under judgement.</param>
    /// <param name="applicable">The table's permissive policies that reach the runtime role.</param>
    /// <param name="displayPredicate">The rule's own predicate, as a migration writes it.</param>
    /// <param name="narrowerPredicate">A second accepted predicate, or null when there is none.</param>
    /// <param name="violations">The report's violation list to append to.</param>
    private static void AddUnscopedPermissiveViolations(
        string table,
        IReadOnlyList<PolicyShape> applicable,
        string displayPredicate,
        string? narrowerPredicate,
        List<IsolationViolation> violations)
    {
        foreach (var policy in applicable.Where(policy => !IsScopedForItsCommands(policy, displayPredicate, narrowerPredicate)))
        {
            violations.Add(new IsolationViolation(
                table,
                IsolationSelfCheckCodes.PermissivePolicyUnscoped,
                $"permissive policy '{policy.Name}' applies to {ApplicationRole} but is not scoped by '{displayPredicate}' — permissive policies OR-combine, so it widens the rows this policy reaches"));
        }
    }

    /// <summary>
    /// True when every clause PostgreSQL consults for the policy's command carries one of the
    /// predicates: USING for the commands that filter rows (SELECT/UPDATE/DELETE and
    /// <c>FOR ALL</c>), WITH CHECK for the commands that admit rows (INSERT/UPDATE and
    /// <c>FOR ALL</c>, reusing USING when none is declared). A policy is only judged for the
    /// commands it governs, so an INSERT-only policy with a scoped WITH CHECK is scoped, while
    /// a DELETE-only policy with <c>using (true)</c> is not.
    /// </summary>
    private static bool IsScopedForItsCommands(PolicyShape policy, string predicate, string? narrowerPredicate)
    {
        var usingScoped = !policy.UsesUsing
            || MatchesUsing(policy, predicate)
            || (narrowerPredicate is not null && MatchesUsing(policy, narrowerPredicate));

        var checkScoped = !policy.UsesWithCheck
            || CheckSatisfied(policy, predicate)
            || (narrowerPredicate is not null && CheckSatisfied(policy, narrowerPredicate));

        return usingScoped && checkScoped;
    }

    /// <summary>
    /// True when <paramref name="expression"/> — a policy clause as <c>pg_get_expr</c> writes
    /// it — admits exactly the rows <paramref name="predicate"/> does or a subset of them: the
    /// expression is the predicate (allowing for how PostgreSQL parenthesizes it), or it is an
    /// <c>AND</c> chain in which one operand — possibly a nested <c>AND</c> chain of its own —
    /// is the predicate.
    /// <para>
    /// The decision is structural, not textual. A match on the paren-stripped text accepts
    /// <c>(pred AND x) OR true</c>: stripping the parentheses removes the grouping, the text
    /// then starts with <c>pred AND</c>, and a clause that opens every row reads as scoped.
    /// So the clause is split at the parentheses of its own depth instead — any top-level
    /// <c>OR</c> refuses it, and only operands of a top-level <c>AND</c> are compared with the
    /// predicate. Parentheses are removed only where they wrap a whole expression, and only
    /// to compare that expression with the predicate; they are never removed before the
    /// top-level operator is read.
    /// </para>
    /// <para>
    /// This is the one implementation the boot check and the 02.6.1-T2 CI lint share
    /// (<c>RlsPolicyCoverageTests</c> calls this method), so a migration and a hand-run
    /// policy change cannot be judged by two different rules.
    /// </para>
    /// </summary>
    /// <param name="expression">A USING or WITH CHECK clause from <c>pg_get_expr</c>.</param>
    /// <param name="predicate">The predicate a migration writes, e.g. <c>org_id = evidgate.current_org_id()</c>.</param>
    public static bool MatchesPredicate(string expression, string predicate) =>
        IsScopedClause(StripOuterParentheses(Normalize(expression)), predicate);

    /// <summary>
    /// True when <paramref name="clause"/> is the predicate, or a top-level <c>AND</c> chain
    /// one of whose operands is a scoped clause. Nothing at the top level may be an <c>OR</c>,
    /// and an operand only continues the chain when it is itself an all-<c>AND</c> chain, so
    /// the predicate is reached through conjunctions alone. That is exactly "the clause is
    /// strictly narrower than the predicate": with <c>OR</c> at any level on the way down the
    /// other operands would widen the rows back out.
    /// <para>
    /// The recursion follows PostgreSQL's deparser, which preserves what the migration wrote:
    /// <c>using (a = 1 and (org_id = evidgate.current_org_id() and status is not null))</c>
    /// comes back as a nested <c>AND</c>, and refusing it would be a false alarm — the clause
    /// still admits only the current org's rows.
    /// </para>
    /// </summary>
    private static bool IsScopedClause(string clause, string predicate)
    {
        if (SameExpression(clause, predicate))
        {
            return true;
        }

        var topLevel = SplitTopLevel(clause);

        return topLevel.Operands.Count > 1
            && topLevel.Operators.All(op => op == AndOperator)
            && topLevel.Operands.Any(operand => IsScopedClause(StripOuterParentheses(operand), predicate));
    }

    /// <summary>
    /// True when two expressions are the same boolean expression: equal after normalisation,
    /// or a homogeneous <c>AND</c>/<c>OR</c> chain of the same operator whose operands are
    /// pairwise the same (`a and (b and c)` is `a and b and c`). Nothing else is equal — a
    /// clause with a different top-level operator is a different clause however it is
    /// parenthesized. Only grouping parentheses may differ; the operands themselves must match
    /// token for token.
    /// </summary>
    private static bool SameExpression(string left, string right)
    {
        var leftExpression = StripOuterParentheses(Normalize(left));
        var rightExpression = StripOuterParentheses(Normalize(right));

        if (string.Equals(leftExpression, rightExpression, StringComparison.Ordinal))
        {
            return true;
        }

        var leftParts = SplitTopLevel(leftExpression);
        var rightParts = SplitTopLevel(rightExpression);

        if (leftParts.Operators.Count == 0 || rightParts.Operators.Count == 0)
        {
            return false;
        }

        var chainOperator = leftParts.Operators[0];

        if (leftParts.Operators.Any(candidate => candidate != chainOperator)
            || rightParts.Operators.Any(candidate => candidate != chainOperator))
        {
            return false;
        }

        var leftChain = Flatten(leftExpression, chainOperator);
        var rightChain = Flatten(rightExpression, chainOperator);

        return leftChain.Count == rightChain.Count
            && leftChain.Zip(rightChain).All(pair => SameExpression(pair.First, pair.Second));
    }

    /// <summary>
    /// The operands of a homogeneous <c>AND</c>/<c>OR</c> chain, with chains of the same
    /// operator nested inside it flattened: <c>a and (b and c)</c> is <c>a</c>, <c>b</c>, <c>c</c>.
    /// An expression whose top level is anything else is returned as its own single operand.
    /// </summary>
    private static List<string> Flatten(string expression, string chainOperator)
    {
        var parts = SplitTopLevel(StripOuterParentheses(expression));

        if (parts.Operators.Count == 0 || parts.Operators.Any(candidate => candidate != chainOperator))
        {
            return [expression];
        }

        return parts.Operands.SelectMany(operand => Flatten(operand, chainOperator)).ToList();
    }

    /// <summary>
    /// Splits a normalized expression at the boolean operators of its own parenthesis depth:
    /// the operands, and the operators that stand between them. Quoted text is skipped, so a
    /// parenthesis, a quote or the word <c>and</c> inside a string literal or a quoted
    /// identifier cannot be mistaken for structure, and a whole word is read at a time so
    /// <c>brand</c> or <c>mandate(x)</c> never reads as <c>and</c>.
    /// </summary>
    private static TopLevelParts SplitTopLevel(string expression)
    {
        var operators = new List<string>();
        var operands = new List<string>();
        var start = 0;
        var depth = 0;

        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];

            if (character is '\'' or '"')
            {
                index = SkipQuoted(expression, index);
                continue;
            }

            if (character == '(')
            {
                depth += 1;
                continue;
            }

            if (character == ')')
            {
                depth -= 1;
                continue;
            }

            if (depth != 0 || !char.IsLetter(character))
            {
                continue;
            }

            var end = index;
            while (end < expression.Length && IsWordCharacter(expression[end]))
            {
                end += 1;
            }

            var word = expression[index..end];
            if (word == AndOperator || word == OrOperator)
            {
                operators.Add(word);
                operands.Add(expression[start..index].Trim());
                start = end;
            }

            index = end - 1;
        }

        operands.Add(expression[start..].Trim());

        return new TopLevelParts(operators, operands);
    }

    /// <summary>The index of the quote that closes the one at <paramref name="start"/>, a doubled quote counting as one.</summary>
    private static int SkipQuoted(string expression, int start)
    {
        var quote = expression[start];

        for (var index = start + 1; index < expression.Length; index += 1)
        {
            if (expression[index] != quote)
            {
                continue;
            }

            if (index + 1 < expression.Length && expression[index + 1] == quote)
            {
                index += 1;
                continue;
            }

            return index;
        }

        return expression.Length;
    }

    /// <summary>
    /// The expression with every pair of parentheses that wraps it whole removed
    /// (<c>(a and b)</c> is <c>a and b</c>), leaving the grouping inside it alone.
    /// </summary>
    private static string StripOuterParentheses(string expression)
    {
        while (expression.Length > 1
            && expression[0] == '('
            && expression[expression.Length - 1] == ')'
            && MatchingParenthesis(expression) == expression.Length - 1)
        {
            expression = expression[1..^1].Trim();
        }

        return expression;
    }

    /// <summary>
    /// The index of the parenthesis that matches the opening one at index 0, or <c>-1</c> when
    /// the expression does not open a parenthesized group. Quoted text is skipped, so a
    /// parenthesis in a string literal cannot close the group.
    /// </summary>
    private static int MatchingParenthesis(string expression)
    {
        if (expression.Length == 0 || expression[0] != '(')
        {
            return -1;
        }

        var depth = 0;

        for (var index = 0; index < expression.Length; index += 1)
        {
            var character = expression[index];

            if (character is '\'' or '"')
            {
                index = SkipQuoted(expression, index);
                continue;
            }

            if (character == '(')
            {
                depth += 1;
            }
            else if (character == ')')
            {
                depth -= 1;

                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    /// <summary>
    /// The expression with runs of whitespace collapsed to one space, trimmed and lowered, so
    /// the comparison is about the clause and not about the case or the spacing
    /// <c>pg_get_expr</c> happens to emit. Parentheses are kept — they are the grouping the
    /// structural comparison reads — and only <see cref="StripOuterParentheses"/> removes a
    /// redundant pair.
    /// </summary>
    private static string Normalize(string expression)
    {
        var builder = new StringBuilder(expression.Length);
        var pendingSpace = false;

        foreach (var character in expression)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>One split of an expression: the operands at the expression's own parenthesis depth, and the operators between them.</summary>
    private sealed record TopLevelParts(IReadOnlyList<string> Operators, IReadOnlyList<string> Operands);

    /// <summary>The <c>AND</c> operator as <c>Normalize</c> leaves it.</summary>
    private const string AndOperator = "and";

    /// <summary>The <c>OR</c> operator as <c>Normalize</c> leaves it: any top-level one refuses a clause.</summary>
    private const string OrOperator = "or";

    private static async Task<List<TableShape>> ReadTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   exists (select 1
                             from pg_catalog.pg_attribute a
                            where a.attrelid = c.oid
                              and a.attname = 'org_id'
                              and a.attnum > 0
                              and not a.attisdropped)
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;

        var tables = new List<TableShape>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new TableShape(
                await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<bool>(1, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<bool>(2, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<bool>(3, cancellationToken).ConfigureAwait(false)));
        }

        return tables;
    }

    private static async Task<List<PolicyShape>> ReadPoliciesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   p.polname,
                   p.polcmd::text,
                   p.polpermissive,
                   pg_catalog.pg_get_expr(p.polqual, p.polrelid),
                   pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid),
                   coalesce((select array_agg(coalesce(r.rolname, 'public')::text
                                                order by coalesce(r.rolname, 'public')::text)
                               from unnest(p.polroles) as u(roleoid)
                               left join pg_catalog.pg_roles r on r.oid = u.roleoid),
                            array[]::text[])
              from pg_catalog.pg_policy p
              join pg_catalog.pg_class c on c.oid = p.polrelid
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1, 2
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;

        var policies = new List<PolicyShape>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            policies.Add(new PolicyShape(
                await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<string>(1, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<string>(2, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<bool>(3, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                    ? null
                    : await reader.GetFieldValueAsync<string>(4, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                    ? null
                    : await reader.GetFieldValueAsync<string>(5, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<string[]>(6, cancellationToken).ConfigureAwait(false)));
        }

        return policies;
    }

    private sealed record TableShape(
        string Name,
        bool RowLevelSecurityEnabled,
        bool RowLevelSecurityForced,
        bool HasOrgId);

    /// <summary>
    /// One policy of one table, with everything the rules need: which roles it applies
    /// to, which commands it governs, whether it is permissive, and its two expressions.
    /// </summary>
    /// <param name="Table">Schema-qualified table the policy belongs to.</param>
    /// <param name="Name">The policy's own name, so a violation names the policy to fix.</param>
    /// <param name="Command"><c>pg_policy.polcmd</c>: <c>*</c> all, <c>r</c> select, <c>a</c> insert, <c>w</c> update, <c>d</c> delete.</param>
    /// <param name="Permissive"><c>pg_policy.polpermissive</c>; permissive policies are OR-ed, restrictive ones AND-ed.</param>
    /// <param name="Using">The USING expression, or null when the policy declares none.</param>
    /// <param name="WithCheck">The WITH CHECK expression, or null when the policy declares none (PostgreSQL then reuses USING).</param>
    /// <param name="Roles">The roles the policy names, with <c>public</c> standing for the PUBLIC pseudo-role (<c>polroles</c> 0).</param>
    private sealed record PolicyShape(
        string Table,
        string Name,
        string Command,
        bool Permissive,
        string? Using,
        string? WithCheck,
        string[] Roles)
    {
        /// <summary>True when the policy applies to <paramref name="role"/> or to PUBLIC — the two ways a policy reaches the runtime role.</summary>
        internal bool AppliesTo(string role, string publicRole) =>
            Roles.Contains(role, StringComparer.Ordinal) || Roles.Contains(publicRole, StringComparer.Ordinal);

        /// <summary>True for <c>polcmd = '*'</c>: the only command that covers reads and writes alike.</summary>
        internal bool IsAllCommands => string.Equals(Command, "*", StringComparison.Ordinal);

        /// <summary>
        /// True when PostgreSQL consults USING for this command (<c>FOR ALL</c>, SELECT, UPDATE,
        /// DELETE): a policy whose USING is not scoped then exposes every row on those commands.
        /// </summary>
        internal bool UsesUsing => IsAllCommands
            || string.Equals(Command, "r", StringComparison.Ordinal)
            || string.Equals(Command, "w", StringComparison.Ordinal)
            || string.Equals(Command, "d", StringComparison.Ordinal);

        /// <summary>
        /// True when PostgreSQL consults WITH CHECK for this command (<c>FOR ALL</c>, INSERT,
        /// UPDATE): a policy with no usable WITH CHECK accepts another org's row on those commands.
        /// </summary>
        internal bool UsesWithCheck => IsAllCommands
            || string.Equals(Command, "a", StringComparison.Ordinal)
            || string.Equals(Command, "w", StringComparison.Ordinal);
    }

    /// <summary>
    /// The five violation codes of one scoped-policy rule set. A table kind whose runtime
    /// policy must carry a predicate has the same five failure modes, so the rule is written
    /// once and spoken in two vocabularies: <c>tenant</c> (AC1) and <c>platform</c> (the
    /// tenant root's self-scope, TL T4/D7).
    /// </summary>
    /// <param name="Missing">No policy carries the predicate at all.</param>
    /// <param name="WrongRole">The predicate exists, but on a policy the runtime role cannot use.</param>
    /// <param name="NotAllCommands">No runtime-role policy covers every command.</param>
    /// <param name="UsingNotScoped">The runtime-role <c>FOR ALL</c> policy's USING is not the predicate.</param>
    /// <param name="WithCheckNotScoped">Its declared WITH CHECK is not the predicate.</param>
    private readonly record struct ScopedPolicyCodes(
        string Missing,
        string WrongRole,
        string NotAllCommands,
        string UsingNotScoped,
        string WithCheckNotScoped);
}

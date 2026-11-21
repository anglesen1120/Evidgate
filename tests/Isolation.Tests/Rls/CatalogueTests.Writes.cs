using System.Globalization;
using Shouldly;

namespace Isolation.Tests;

/// <summary>
/// The write half of the catalogue probes: every command the application role attempts on a catalogue
/// table is refused — the missing privilege, a silent zero-row no-op, or both depending on the table —
/// and the built-in rows are unchanged afterwards.
/// </summary>
public sealed partial class CatalogueTests
{
    /// <summary><c>public.permission</c>: 0010 revokes INSERT/UPDATE/DELETE from the app role, so every command is a 42501.</summary>
    private async Task CheckPermissionWritesAsync(List<string> failures)
    {
        const string table = "public.permission";
        var before = await ReadKeysAsync(table, "true", "code");
        before.ShouldNotBeEmpty($"{table}: the built-in catalogue is empty — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.permission (code, resource, action, description) values (@key, 'probe', 'probe', 'probe')",
            ("key", "probe:probe"));
        AssertRefused(table, "INSERT of a built-in row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.permission set description = description where code = @key",
            ("key", before[0]));
        AssertRefused(table, "UPDATE of a built-in row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.permission where code = @key",
            ("key", before[0]));
        AssertRefused(table, "DELETE of a built-in row", delete, silentAllowed: true, failures);

        // TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0010 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.permission");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "true", "code", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.role</c>: the app role may write only rows of its own org, so an insert of a
    /// built-in row (<c>org_id</c> NULL) fails the <c>WITH CHECK</c> and an update of one changes
    /// zero rows; DELETE is revoked outright.
    /// </summary>
    private async Task CheckRoleWritesAsync(List<string> failures)
    {
        const string table = "public.role";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the built-in catalogue is empty — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@key::uuid, null, @code, 'Probe', true, 'member')",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ("code", "probe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]));
        AssertRefused(table, "INSERT of a built-in row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.role set name = name where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "UPDATE of a built-in row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.role where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "DELETE of a built-in row", delete, silentAllowed: true, failures);

        // TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0010 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.role");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.role_permission</c>: same posture as <c>role</c>, plus the tie that the referenced
    /// role must itself be an org row of the caller's org — so a grant can never be attached to a
    /// built-in role. The insert uses a (built-in role, permission) pair that is not already granted,
    /// so the only possible refusal is the policy, never the unique key.
    /// </summary>
    private async Task CheckRolePermissionWritesAsync(List<string> failures)
    {
        const string table = "public.role_permission";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the built-in catalogue is empty — the write probe would have nothing to target");

        var pair = await ReadUnGrantedPairAsync();
        pair.ShouldNotBeNullOrWhiteSpace(
            "no un-granted (built-in role, permission) pair exists: the INSERT probe cannot be built");

        var parts = pair!.Split('|', 2);
        var insert = await RefusalAsync(
            "insert into public.role_permission (id, org_id, role_id, permission_code) values (@key::uuid, null, @role_id::uuid, @permission_code)",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ("role_id", parts[0]),
            ("permission_code", parts[1]));
        AssertRefused(table, "INSERT of a built-in row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.role_permission set permission_code = permission_code where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "UPDATE of a built-in row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.role_permission where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "DELETE of a built-in row", delete, silentAllowed: true, failures);

        // TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0010 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.role_permission");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.healing_switch</c> (0052): the system row is the platform's, so an app-role INSERT of
    /// another one fails the <c>WITH CHECK</c> (<c>org_id</c> NULL never equals the current org), an
    /// UPDATE of it matches zero rows, and DELETE is revoked outright.
    /// </summary>
    private async Task CheckHealingSwitchWritesAsync(List<string> failures)
    {
        const string table = "public.healing_switch";
        var before = await ReadKeysAsync(table, "scope = 'system'", "id::text");
        before.ShouldNotBeEmpty($"{table}: the system switch row is missing — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at) values (@key::uuid, 'system', null, null, 'disabled', @key::uuid, now())",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)));
        AssertRefused(table, "INSERT of a second system row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.healing_switch set mode = 'disabled' where scope = 'system'");
        AssertRefused(table, "UPDATE of the system row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.healing_switch where scope = 'system'");
        AssertRefused(table, "DELETE of the system row", delete, silentAllowed: true, failures);

        // TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0052 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.healing_switch");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "scope = 'system'", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.usage_price</c> (0073): the platform rate-card rows (<c>org_id</c> NULL) are readable by
    /// every org and the app role can neither insert one (the <c>WITH CHECK</c> never matches a NULL org),
    /// nor update or delete (no privilege).
    /// </summary>
    private async Task CheckUsagePriceWritesAsync(List<string> failures)
    {
        const string table = "public.usage_price";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the platform price row is missing — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.usage_price (id, org_id, metric, unit_price, currency, effective_from, rate_card_version) values (@key::uuid, null, 'egress_gb', 1, 'USD', now(), 'probe')",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)));
        AssertRefused(table, "INSERT of a platform row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync("update public.usage_price set unit_price = 2 where org_id is null");
        AssertRefused(table, "UPDATE of a platform row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync("delete from public.usage_price where org_id is null");
        AssertRefused(table, "DELETE of a platform row", delete, silentAllowed: true, failures);

        var truncate = await RefusalAsync("truncate public.usage_price");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.report_parser</c> (0075): the registry is a platform catalogue, so the app role can
    /// neither insert a parser row (the <c>WITH CHECK</c> never matches a NULL org) nor update or
    /// delete one (no privilege).
    /// </summary>
    private async Task CheckReportParserWritesAsync(List<string> failures)
    {
        const string table = "public.report_parser";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the built-in parser row is missing — the write probe would have nothing to target");

        // A parser id no row registers, so the only possible refusal is the policy, never the unique (parser_id, version).
        var insert = await RefusalAsync(
            "insert into public.report_parser (id, org_id, parser_id, version, producer_kind, schema_version, notes) values (@key::uuid, null, @parser_id, '1.0.0', 'probe', 1, 'probe')",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ("parser_id", "probe-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]));
        AssertRefused(table, "INSERT of a platform parser row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync("update public.report_parser set notes = notes where org_id is null");
        AssertRefused(table, "UPDATE of a platform parser row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync("delete from public.report_parser where org_id is null");
        AssertRefused(table, "DELETE of a platform parser row", delete, silentAllowed: true, failures);

        var truncate = await RefusalAsync("truncate public.report_parser");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>The app role holds SELECT only on the registry: INSERT, UPDATE and DELETE are revoked, so every
    /// write probe fails with <c>42501</c> and no row-level policy is consulted.</summary>
    private async Task CheckTemplateWritesAsync(List<string> failures)
    {
        const string table = "public.template";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the built-in template row is missing — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules) values (@key::uuid, null, 'web', 'probe-template', 1, @body_ref, '{}'::jsonb)",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ("body_ref", "sha256:" + new string('d', 64)));
        AssertRefused(table, "INSERT of a platform template row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync("update public.template set name = name where org_id is null");
        AssertRefused(table, "UPDATE of a platform template row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync("delete from public.template where org_id is null");
        AssertRefused(table, "DELETE of a platform template row", delete, silentAllowed: true, failures);

        var truncate = await RefusalAsync("truncate public.template");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }
}

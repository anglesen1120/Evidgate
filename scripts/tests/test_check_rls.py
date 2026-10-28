"""Negative fixtures for scripts/check-rls.py (02.6.1-T2.2): each missing property fails
with its own rule and names the table or function; the exception list excuses a table."""
import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("check_rls", Path(__file__).resolve().parent.parent / "check-rls.py")
check_rls = importlib.util.module_from_spec(SPEC)
sys.modules["check_rls"] = check_rls
SPEC.loader.exec_module(check_rls)

GOOD_TABLE = """
create table public.widget (
  id uuid not null,
  org_id uuid not null,
  constraint widget_pkey primary key (id),
  constraint widget_org_id_key unique (org_id, id)
);
alter table public.widget enable row level security;
alter table public.widget force row level security;
create policy widget_app_tenant on public.widget for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
"""

GOOD_DEFINER = """
create or replace function evidgate_resolve.lookup(p_key text) returns uuid
  language sql security definer set search_path = '' as $$
  insert into public.audit_log (k) values (p_key);
  select m.org_id from public.membership m where m.invite_secret_prefix = p_key
$$;
revoke all on function evidgate_resolve.lookup(text) from public;
grant execute on function evidgate_resolve.lookup(text) to evidgate_app;
"""


def run(sql: str, exceptions: str = "") -> list:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "0001_x_y.sql").write_text(sql, encoding="utf-8")
        (root / "ex.txt").write_text(exceptions, encoding="utf-8")
        return check_rls.lint(root, root / "ex.txt")


def rules(findings) -> set:
    return {f.rule for f in findings}


class TableRules(unittest.TestCase):
    def test_complete_table_is_clean(self):
        self.assertEqual(run(GOOD_TABLE), [])

    def test_missing_org_id_is_named(self):
        sql = GOOD_TABLE.replace("  org_id uuid not null,\n", "").replace("unique (org_id, id)", "unique (id)")
        findings = run(sql)
        self.assertIn("missing_org_id", rules(findings))
        self.assertTrue(all(f.subject == "public.widget" for f in findings))

    def test_missing_unique_key(self):
        findings = run(GOOD_TABLE.replace(",\n  constraint widget_org_id_key unique (org_id, id)", ""))
        self.assertEqual(rules(findings), {"missing_unique_key"})

    def test_unique_index_with_if_not_exists_counts(self):
        sql = GOOD_TABLE.replace(",\n  constraint widget_org_id_key unique (org_id, id)", "") + (
            "create unique index if not exists widget_org_id_uq on public.widget (org_id, id);"
        )
        self.assertEqual(run(sql), [])

    def test_missing_force_rls(self):
        findings = run(GOOD_TABLE.replace("alter table public.widget force row level security;", ""))
        self.assertEqual(rules(findings), {"missing_rls"})

    def test_missing_policy(self):
        sql = GOOD_TABLE.split("create policy")[0]
        self.assertEqual(rules(run(sql)), {"missing_policy"})

    def test_helper_in_another_statement_does_not_satisfy_the_policy(self):
        sql = GOOD_TABLE.replace("evidgate.current_org_id()", "true") + (
            "create policy other on public.gadget using (org_id = evidgate.current_org_id());"
        )
        self.assertIn("missing_policy", rules(run(sql)))

    def test_open_policy_for_app_is_refused(self):
        sql = GOOD_TABLE + "create policy wide on public.widget for all using (true);"
        self.assertIn("open_policy", rules(run(sql)))

    def test_open_policy_for_owner_only_is_allowed(self):
        sql = GOOD_TABLE + "create policy owner_all on public.widget for all to evidgate_owner using (true) with check (true);"
        self.assertEqual(run(sql), [])

    def test_a_later_disable_or_no_force_is_detected(self):
        self.assertEqual(rules(run(GOOD_TABLE + "alter table public.widget no force row level security;")), {"missing_rls"})
        self.assertEqual(rules(run(GOOD_TABLE + "alter table public.widget disable row level security;")), {"missing_rls"})

    def test_unqualified_table_is_linted_as_public(self):
        sql = GOOD_TABLE.replace("public.widget", "widget")
        self.assertEqual(run(sql), [])
        self.assertIn("missing_rls", rules(run(sql.split("alter table")[0])))

    def test_quoted_reserved_word_table_is_qualified_without_quotes(self):
        sql = GOOD_TABLE.replace("public.widget", 'public."user"')
        self.assertEqual(run(sql), [])
        self.assertIn("missing_rls", rules(run(sql.split("alter table")[0])))

    def test_policy_without_the_helper_is_not_a_tenant_policy(self):
        sql = GOOD_TABLE.replace("evidgate.current_org_id()", "current_setting('evidgate.org_id')::uuid")
        self.assertEqual(rules(run(sql)), {"missing_policy"})

    def test_exception_list_excuses_org_id_and_key_but_not_rls(self):
        sql = """
        create table public.session (id uuid not null, constraint session_pkey primary key (id));
        """
        self.assertEqual(rules(run(sql, "public.session platform\n")), {"missing_rls", "missing_policy"})
        mechanism = run(sql, "public.session mechanism\n")
        self.assertEqual(mechanism, [])

    def test_comments_do_not_satisfy_the_lint(self):
        sql = GOOD_TABLE.replace("alter table public.widget force row level security;", "-- alter table public.widget force row level security;")
        self.assertEqual(rules(run(sql)), {"missing_rls"})


class DefinerRules(unittest.TestCase):
    def test_complete_definer_is_clean(self):
        self.assertEqual(run(GOOD_DEFINER), [])

    def test_missing_search_path(self):
        findings = run(GOOD_DEFINER.replace("set search_path = ''", ""))
        self.assertEqual(rules(findings), {"definer_search_path"})

    def test_public_grant(self):
        sql = GOOD_DEFINER + "grant execute on function evidgate_resolve.lookup(text) to public;"
        self.assertIn("definer_public_grant", rules(run(sql)))

    def test_unexpected_grantee(self):
        sql = GOOD_DEFINER + "grant execute on function evidgate_resolve.lookup(text) to somebody;"
        self.assertEqual(rules(run(sql)), {"definer_grantee"})

    def test_unqualified_relation(self):
        findings = run(GOOD_DEFINER.replace("from public.membership m", "from membership m"))
        self.assertEqual(rules(findings), {"definer_unqualified"})

    def test_a_cte_name_is_not_an_unqualified_relation(self):
        sql = GOOD_DEFINER.replace(
            "select m.org_id from public.membership m where m.invite_secret_prefix = p_key",
            "with hit as (select m.org_id from public.membership m) select h.org_id from hit h",
        )
        self.assertEqual(run(sql), [])

    def test_missing_audit_row(self):
        findings = run(GOOD_DEFINER.replace("insert into public.audit_log (k) values (p_key);", ""))
        self.assertEqual(rules(findings), {"definer_audit"})

    def test_documented_deferral_excuses_the_audit_row(self):
        sql = "-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.lookup\n" + GOOD_DEFINER.replace(
            "insert into public.audit_log (k) values (p_key);", ""
        )
        self.assertEqual(run(sql), [])

    def test_deferral_is_per_function(self):
        sql = "-- lint: audit-deferred 02.7.1-T2.2 evidgate_resolve.other\n" + GOOD_DEFINER.replace(
            "insert into public.audit_log (k) values (p_key);", ""
        )
        self.assertEqual(rules(run(sql)), {"definer_audit"})

    def test_generic_dollar_tag_cannot_hide_a_definer(self):
        sql = GOOD_DEFINER.replace("$$", "$fn$").replace("set search_path = ''", "")
        self.assertEqual(rules(run(sql)), {"definer_search_path"})

    def test_unqualified_function_name_is_still_linted(self):
        sql = GOOD_DEFINER.replace("evidgate_resolve.lookup", "lookup").replace("set search_path = ''", "")
        self.assertIn("definer_search_path", rules(run(sql)))

    def test_invoker_functions_are_not_linted(self):
        sql = "create function public.f() returns int language sql as $$ select 1 $$;"
        self.assertEqual(run(sql), [])


EXEMPT_DEFINER = """
create or replace function {name}(p_key text) returns uuid
  language sql security definer set search_path = '' as $$
  select p_key::uuid
$$;
"""


class AuditExemptions(unittest.TestCase):
    """F28/`0051`: the audit exemptions are exact, qualified and reasoned, and they do not blanket.

    `DEFINER_AUDIT_EXEMPT` re-points the 02.7.1-T2.2 deferrals of the session family, whose
    receiver task is merged (the reason is now the shape of the call, not a pending task). The
    set is keyed by the EXACT qualified name the migration defines, so these cases pin both
    halves: a listed definer needs no row, and an unlisted one — including a same-spelling
    function in another schema — still fails.
    """

    def test_every_exempt_definer_is_accepted_and_reasoned(self):
        self.assertNotIn("evidgate_resolve.session_revoke_one", check_rls.DEFINER_AUDIT_EXEMPT,
                         "the administrative revoke owes a per-call row (0050/0051): it is not exempt")

        for name, reason in check_rls.DEFINER_AUDIT_EXEMPT.items():
            with self.subTest(name=name):
                self.assertIn(".", name, "an exemption is keyed by `schema.function`, never a bare name")
                self.assertNotIn("*", name, "an exemption is an exact name, never a pattern")
                self.assertTrue(reason.strip(), f"{name} carries its reason")
                self.assertEqual(run(EXEMPT_DEFINER.format(name=name)), [], f"{name} is exempt from definer_audit")

    def test_a_new_unlisted_definer_still_needs_its_audit_row(self):
        findings = run(EXEMPT_DEFINER.format(name="evidgate_resolve.brand_new_definer"))
        self.assertEqual(rules(findings), {"definer_audit"})

    def test_an_exemption_does_not_cover_another_schema(self):
        findings = run(EXEMPT_DEFINER.format(name="evidgate.session_list"))
        self.assertEqual(rules(findings), {"definer_audit"}, "the key is the qualified name, not the bare one")

    def test_the_healing_switch_definer_is_exempt_only_under_its_exact_name(self):
        """08.6.3/`0052`: the tenant-less system switch has no org chain to write into.

        The exemption has to stay as narrow as the reason: the same spelling in another schema, and
        a neighbour whose name merely starts with it, are both ordinary definers that owe an audit
        row.
        """
        self.assertIn("evidgate.healing_switch_set_system", check_rls.DEFINER_AUDIT_EXEMPT)
        self.assertEqual(run(EXEMPT_DEFINER.format(name="evidgate.healing_switch_set_system")), [])

        for unlisted in (
            "public.healing_switch_set_system",
            "evidgate.healing_switch_set_system_v2",
            "evidgate.healing_switch_set_tenant",
        ):
            with self.subTest(name=unlisted):
                self.assertEqual(rules(run(EXEMPT_DEFINER.format(name=unlisted))), {"definer_audit"})


class RealRepository(unittest.TestCase):
    def test_migrations_of_this_repository_are_clean(self):
        root = Path(__file__).resolve().parent.parent.parent
        findings = check_rls.lint(root / "migrations", root / "contracts" / "rls-exceptions.txt")
        self.assertEqual([str(f) for f in findings], [])


if __name__ == "__main__":
    unittest.main()

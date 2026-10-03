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
        sql = GOOD_TABLE.replace("force row level security;", "-- force row level security;")
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

    def test_missing_audit_row(self):
        findings = run(GOOD_DEFINER.replace("insert into public.audit_log (k) values (p_key);", ""))
        self.assertEqual(rules(findings), {"definer_audit"})

    def test_documented_deferral_excuses_the_audit_row(self):
        sql = "-- lint: audit-deferred 02.7.1-T2.2\n" + GOOD_DEFINER.replace(
            "insert into public.audit_log (k) values (p_key);", ""
        )
        self.assertEqual(run(sql), [])

    def test_invoker_functions_are_not_linted(self):
        sql = "create function public.f() returns int language sql as $$ select 1 $$;"
        self.assertEqual(run(sql), [])


class RealRepository(unittest.TestCase):
    def test_migrations_of_this_repository_are_clean(self):
        root = Path(__file__).resolve().parent.parent.parent
        findings = check_rls.lint(root / "migrations", root / "contracts" / "rls-exceptions.txt")
        self.assertEqual([str(f) for f in findings], [])


if __name__ == "__main__":
    unittest.main()

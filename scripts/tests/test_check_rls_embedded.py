"""Fixtures for the SQL a migration runs from inside a `do` body or an `execute` string.

Loaded by `scripts/tests/test_check_rls.py`, because `verify.sh` runs that file by name and unittest
collects the `TestCase` classes imported into it. Every case pins both halves: the hidden statement
fails with the named rule, and the same file without it is clean.
"""
import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent


def _check_rls():
    """The entry module, shared with `test_check_rls.py`, which loads it by path first."""
    if "check_rls" in sys.modules:
        return sys.modules["check_rls"]
    spec = importlib.util.spec_from_file_location("check_rls", HERE.parent / "check-rls.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules["check_rls"] = module
    spec.loader.exec_module(module)
    return module


check_rls = _check_rls()

TABLE = """
create table public.widget (
  id uuid not null,
  org_id uuid not null,
  constraint widget_pkey primary key (id),
  constraint widget_org_key unique (org_id, id)
);
alter table public.widget enable row level security;
alter table public.widget force row level security;
create policy widget_app_tenant on public.widget for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
"""

OPERATOR_SELECT = """
alter default privileges for role evidgate_owner in schema public
  grant select on tables to evidgate_operator;
"""

WIDE = "create policy widget_wide on public.widget for all using (true)"
WIDE_RULES = {"policy_no_role", "open_policy"}  # no `TO` clause, and the predicate is open

SAFE_OPERATOR = (
    "create policy widget_operator_read on public.widget for select to evidgate_operator "
    "using (org_id = '00000000-0000-0000-0000-000000000000'::uuid);"
)
SESSION_OPERATOR = (
    "create policy widget_operator_read on public.widget for select to evidgate_operator "
    "using (org_id = current_setting('evidgate.org_id')::uuid);"
)
REPAIR = (
    "alter policy widget_operator_read on public.widget "
    "using (org_id = '00000000-0000-0000-0000-000000000000'::uuid);"
)


def session_alter() -> str:
    return (
        "do $$ begin alter policy widget_operator_read on public.widget "
        "using (org_id = current_setting('evidgate.org_id')::uuid); end $$;"
    )


def run(sql: str, exceptions: str = "") -> list:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "0001_x_y.sql").write_text(sql, encoding="utf-8")
        (root / "ex.txt").write_text(exceptions, encoding="utf-8")
        return check_rls.lint(root, root / "ex.txt")


def rules(findings) -> set:
    return {f.rule for f in findings}


class EmbeddedPolicyStatements(unittest.TestCase):
    """A policy or grant written as text inside a `do` body or an `execute` runs all the same, so
    the replay reads it; the body of a function is not run, so its text stays text."""

    def test_a_policy_written_in_a_do_body_is_linted(self):
        hidden = run(TABLE + f"do $$ begin {WIDE}; end $$;")
        self.assertEqual(rules(hidden), WIDE_RULES)
        self.assertEqual(run(TABLE), [], "the control: the same file without the body is clean")

    def test_a_generic_dollar_tag_does_not_hide_the_body(self):
        sql = TABLE + f"do $body$ begin if true then {WIDE}; end if; end $body$;"
        self.assertEqual(rules(run(sql)), WIDE_RULES)

    def test_a_role_less_policy_inside_a_do_body_is_refused(self):
        sql = TABLE + "do $$ begin create policy widget_open on public.widget for all using (org_id = 1); end $$;"
        self.assertEqual(rules(run(sql)), {"policy_no_role"})

    def test_an_execute_string_is_linted_in_every_quoting_form(self):
        for statement in ("'", "E'", "$sql$", "$body$"):
            with self.subTest(quoting=statement):
                close = "'" if statement.endswith("'") else statement
                sql = TABLE + f"do $$ begin execute {statement}{WIDE}{close}; end $$;"
                self.assertEqual(rules(run(sql)), WIDE_RULES)

    def test_a_grant_inside_a_do_body_changes_the_operator_reach(self):
        body = "do $$ begin execute 'grant evidgate_app to evidgate_operator'; end $$;"
        self.assertEqual(rules(run(OPERATOR_SELECT + TABLE + body)), {"operator_select_no_policy"})
        self.assertEqual(run(OPERATOR_SELECT + TABLE), [], "the control: no membership, no read path")

    def test_a_table_created_in_a_do_body_is_linted(self):
        findings = run("do $$ begin create table public.gadget (id uuid not null); end $$;")
        self.assertEqual(rules(findings), {"missing_org_id", "missing_unique_key", "missing_rls", "missing_policy"})
        self.assertTrue(all(f.subject == "public.gadget" for f in findings))

    def test_a_function_body_is_not_replayed(self):
        function = (
            "create function public.f() returns void language plpgsql as $$ begin "
            f"execute '{WIDE}'; end $$;"
        )
        self.assertEqual(run(TABLE + function), [], "only a `do` block runs its body at migration time")

    def test_a_keyword_inside_a_literal_is_data(self):
        body = "do $$ begin raise notice 'grant select on public.widget to evidgate_operator'; end $$;"
        self.assertEqual(run(TABLE + body), [])

    def test_a_grant_of_execute_in_a_do_body_is_not_read_as_plpgsql_execute(self):
        body = "do $$ begin grant execute on function public.fn() to evidgate_app; end $$;"
        self.assertEqual(run(TABLE + body), [])
        self.assertEqual(run(TABLE), [], "the control: the same file without the body is clean")

    def test_a_revoke_of_execute_in_a_do_body_is_not_read_as_plpgsql_execute(self):
        body = "do $$ begin revoke execute on function public.fn() from evidgate_app; end $$;"
        self.assertEqual(run(TABLE + body), [])

    def test_an_execute_command_beside_a_privilege_grant_is_still_reported(self):
        body = (
            "do $$ declare stmt text; begin grant execute on function public.fn() to evidgate_app; "
            "execute stmt; end $$;"
        )
        self.assertEqual(rules(run(TABLE + body)), {"embedded_dynamic"})


class UnreadableExecute(unittest.TestCase):
    """A statement the file does not spell out is reported, so an unreadable one cannot pass as
    clean; a literal without DDL in it needs no report."""

    def test_an_execute_of_a_variable_is_reported(self):
        findings = run(TABLE + "do $$ declare stmt text; begin execute stmt; end $$;")
        self.assertEqual(rules(findings), {"embedded_dynamic"})
        self.assertEqual({f.subject for f in findings}, {"execute"})

    def test_an_execute_format_template_that_builds_ddl_is_reported(self):
        body = "do $$ begin execute format('create policy %I on %s for all using (true)', 'p', 'public.widget'); end $$;"
        self.assertEqual(rules(run(TABLE + body)), {"embedded_dynamic"})

    def test_an_execute_of_a_literal_without_ddl_is_clean(self):
        self.assertEqual(run(TABLE + "do $$ begin execute 'select 1'; end $$;"), [])

    def test_an_execute_format_template_without_ddl_is_clean(self):
        body = "do $$ begin execute format('delete from public.widget where id = %L', %L); end $$;"
        self.assertEqual(run(TABLE + body), [])

    def test_an_execute_with_an_into_clause_is_read(self):
        body = "do $$ declare n bigint; begin execute 'select count(*) from public.widget' into n; end $$;"
        self.assertEqual(run(TABLE + body), [])

    def test_an_execute_with_a_bound_parameter_is_reported(self):
        body = (
            "do $$ begin execute 'select id from public.widget where id = $1' "
            "using '00000000-0000-0000-0000-000000000000'::uuid; end $$;"
        )
        self.assertEqual(rules(run(TABLE + body)), {"embedded_dynamic"})


class ExecuteConcatenation(unittest.TestCase):
    """PostgreSQL folds `'a' || 'b'` before it parses the result, so the replay reads the folded
    text; a part the file does not spell out leaves the whole statement unread and reported."""

    def test_a_literal_concatenation_is_read_as_one_statement(self):
        body = (
            "do $$ begin execute 'create policy widget_dyn on public.widget for all "
            "to evidgate_operator ' || 'using (true)'; end $$;"
        )
        self.assertEqual(rules(run(TABLE + body)), {"open_policy"})
        self.assertEqual(run(TABLE), [], "the control: the same file without the body is clean")

    def test_a_concatenation_with_a_variable_is_reported(self):
        body = "do $b$ begin execute 'grant select on ' || t || ' to evidgate_operator'; end $b$;"
        findings = run(TABLE + body)
        self.assertEqual(rules(findings), {"embedded_dynamic"})
        self.assertEqual({f.subject for f in findings}, {"execute"})

    def test_a_literal_concatenation_without_ddl_is_clean(self):
        self.assertEqual(run(TABLE + "do $$ begin execute 'select ' || '1'; end $$;"), [])

    def test_a_literal_with_a_cast_on_one_part_is_reported(self):
        body = "do $$ begin execute 'select 1 ' || 'union all select 2'::text; end $$;"
        self.assertEqual(rules(run(TABLE + body)), {"embedded_dynamic"})


class EmbeddedReplayOrder(unittest.TestCase):
    """An embedded statement takes its place in the replay, so the effective policy is the one the
    last statement left — reading the body out of order would judge a policy that never stood."""

    def test_an_embedded_alter_that_reads_the_session_is_seen(self):
        sql = OPERATOR_SELECT + TABLE + SAFE_OPERATOR + session_alter()
        self.assertEqual(rules(run(sql)), {"operator_select_no_policy"})

    def test_a_later_statement_wins_over_an_embedded_one(self):
        sql = OPERATOR_SELECT + TABLE + SESSION_OPERATOR + session_alter() + REPAIR
        self.assertEqual(run(sql), [])

    def test_the_order_of_the_statements_is_what_counts(self):
        sql = OPERATOR_SELECT + TABLE + SAFE_OPERATOR + REPAIR + session_alter()
        self.assertEqual(rules(run(sql)), {"operator_select_no_policy"})


if __name__ == "__main__":
    unittest.main()

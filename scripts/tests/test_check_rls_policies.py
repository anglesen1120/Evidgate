"""Replay fixtures for `scripts/check-rls.py`: `alter policy`, quoted policy names, the session
pseudo-roles, and the SELECT reach of `evidgate_operator` (CR-03).

Loaded by `scripts/tests/test_check_rls.py`, because `verify.sh` runs that file by name and
unittest collects the `TestCase` classes imported into it. Every case pins both halves: the
unsafe fixture fails with the named rule, the safe one does not.
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


def create_table(name: str) -> str:
    return f"""
create table public.{name} (
  id uuid not null,
  org_id uuid not null,
  constraint {name}_pkey primary key (id),
  constraint {name}_org_key unique (org_id, id)
);
alter table public.{name} enable row level security;
alter table public.{name} force row level security;
"""


def tenant_policy(name: str, roles: str = "to evidgate_app") -> str:
    """A policy over the helper predicate; `roles` empty leaves the `TO` clause out."""
    return (
        f"create policy {name}_tenant on public.{name} for all {roles} "
        "using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());"
    )


def run_files(files: dict[str, str], exceptions: str = "") -> list:
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for name, sql in files.items():
            (root / name).write_text(sql, encoding="utf-8")
        (root / "ex.txt").write_text(exceptions, encoding="utf-8")
        return check_rls.lint(root, root / "ex.txt")


def run(sql: str, exceptions: str = "") -> list:
    return run_files({"0001_x_y.sql": sql}, exceptions)


def rules(findings) -> set:
    return {f.rule for f in findings}


def subjects(findings, rule: str) -> set:
    return {f.subject for f in findings if f.rule == rule}


class PolicyStatementReplay(unittest.TestCase):
    """`alter policy` and quoted names are replayed, so a widening cannot hide behind the
    `create` that was safe, and `to current_user` / `session_user` / `current_role` scope
    nothing — they resolve to whichever role is connected, exactly like PUBLIC."""

    def test_alter_policy_to_public_is_refused_in_the_altering_file(self):
        unsafe = run_files({
            "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
            "0002_widen.sql": "alter policy widget_tenant on public.widget to public;",
        })
        self.assertIn("policy_no_role", rules(unsafe))
        self.assertEqual({f.file for f in unsafe}, {"0002_widen.sql"}, "the widening statement is named, not the safe create")
        safe = run_files({
            "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
            "0002_scope.sql": "alter policy widget_tenant on public.widget to evidgate_app;",
        })
        self.assertEqual(safe, [])

    def test_alter_policy_using_true_is_refused(self):
        unsafe = run_files({
            "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
            "0002_open.sql": "alter policy widget_tenant on public.widget using (true);",
        })
        self.assertEqual(rules(unsafe), {"missing_policy", "open_policy"},
                         "using (true) drops the tenant predicate as well as opening the policy")
        self.assertEqual({f.file for f in unsafe if f.rule == "open_policy"}, {"0002_open.sql"})
        safe = run_files({
            "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
            "0002_reopen.sql": "alter policy widget_tenant on public.widget using (org_id = evidgate.current_org_id());",
        })
        self.assertEqual(safe, [])

    def test_alter_policy_rename_moves_the_effective_policy(self):
        renamed = run_files({
            "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
            "0002_rename.sql": 'alter policy widget_tenant on public.widget rename to "tenant read" to evidgate_app;',
            "0003_drop.sql": 'drop policy if exists "tenant read" on public.widget;',
        })
        self.assertEqual(rules(renamed), {"missing_policy"}, "the rename and the quoted drop meet on the same policy")
        kept = run_files({
            "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
            "0002_rename.sql": 'alter policy widget_tenant on public.widget rename to "tenant read" to evidgate_app;',
        })
        self.assertEqual(kept, [], "a rename that keeps the roles is not a widening")

    def test_a_quoted_policy_name_with_a_space_is_read_whole(self):
        unsafe = run(create_table("widget") + 'create policy "tenant read" on public.widget for all '
                    "using (org_id = evidgate.current_org_id());")
        self.assertEqual(rules(unsafe), {"policy_no_role"}, "the statement is parsed, not skipped as a missing policy")
        safe = run(create_table("widget") + 'create policy "tenant read" on public.widget for all to evidgate_app '
                   "using (org_id = evidgate.current_org_id());")
        self.assertEqual(safe, [])

    def test_a_policy_name_with_a_doubled_quote_round_trips(self):
        name = 'tenant ""read"" scope'
        unsafe = run_files({
            "0001_widget.sql": create_table("widget")
            + f'create policy "{name}" on public.widget for all to evidgate_app '
              "using (org_id = evidgate.current_org_id());",
            "0002_drop.sql": f'drop policy if exists "{name}" on public.widget;',
        })
        self.assertEqual(rules(unsafe), {"missing_policy"}, "the doubled quote reads back as one name")
        safe = run(create_table("widget") + f'create policy "{name}" on public.widget for all to evidgate_app '
                   "using (org_id = evidgate.current_org_id());")
        self.assertEqual(safe, [])

    def test_a_session_pseudo_role_scopes_nothing(self):
        for role in ("current_user", "session_user", "current_role"):
            with self.subTest(role=role):
                self.assertEqual(rules(run(create_table("widget") + tenant_policy("widget", f"to {role}"))),
                                 {"policy_no_role"})
                widened = run_files({
                    "0001_widget.sql": create_table("widget") + tenant_policy("widget"),
                    "0002_widen.sql": f"alter policy widget_tenant on public.widget to {role};",
                })
                self.assertIn("policy_no_role", rules(widened), "an alter to a session role is not a scope either")
        self.assertEqual(run(create_table("widget") + tenant_policy("widget")), [])

    def test_the_exemption_needs_a_replacement_to_evidgate_app(self):
        # `test_check_rls` is loaded before this module when verify.sh runs it by name; the path
        # insert covers running this file on its own, where unittest does not add its directory.
        sys.path.insert(0, str(HERE))
        from test_check_rls import ROLE_SCOPED_EVENT, TO_LESS_EVENT

        self.assertEqual(run_files({
            "0053_security_security_event.sql": TO_LESS_EVENT,
            "0055_tenant_policy_roles.sql": ROLE_SCOPED_EVENT,
        }), [])
        for replacement in ("to evidgate_owner", "to current_user", "to public"):
            with self.subTest(replacement=replacement):
                files = {
                    "0053_security_security_event.sql": TO_LESS_EVENT,
                    "0055_tenant_policy_roles.sql": ROLE_SCOPED_EVENT.replace("to evidgate_app", replacement),
                }
                self.assertEqual(rules(run_files(files)), {"policy_no_role"},
                                 "only a replacement that names evidgate_app supersedes the open statement")


class OperatorSelectReplay(unittest.TestCase):
    """`evidgate_operator` holds SELECT only where the replay says so: table lists, schema-wide
    grants, PUBLIC, role membership, and default privileges that reach later tables only.

    `operator_select_no_policy` adds what `policy_no_role` cannot see — the SELECT reach. A
    column-level revoke removes no table privilege, and a policy scoped to another role leaves
    the operator with zero rows, which is why that shape is not reported.
    """

    def test_a_multi_table_grant_reaches_every_table_it_names(self):
        unsafe = run(create_table("widget") + tenant_policy("widget", "") + create_table("gadget")
                     + tenant_policy("gadget", "") + "grant select on public.widget, public.gadget to evidgate_operator;")
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget", "public.gadget"})
        safe = run(create_table("widget") + tenant_policy("widget", "") + create_table("gadget")
                   + tenant_policy("gadget", "") + "grant select on public.widget, public.gadget to evidgate_operator;"
                   + "revoke all on public.widget, public.gadget from evidgate_operator;")
        self.assertEqual(subjects(safe, "operator_select_no_policy"), set())

    def test_all_tables_in_schema_reaches_the_tables_that_exist(self):
        unsafe = run(create_table("widget") + tenant_policy("widget", "")
                     + "grant select on all tables in schema public to evidgate_operator;"
                     + create_table("gadget") + tenant_policy("gadget", ""))
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget"},
                         "a schema-wide grant cannot reach a table created after it")
        safe = run(create_table("widget") + tenant_policy("widget", "")
                   + "grant select on all tables in schema public to evidgate_operator;"
                   + "revoke select on all tables in schema public from evidgate_operator;"
                   + create_table("gadget") + tenant_policy("gadget", ""))
        self.assertEqual(subjects(safe, "operator_select_no_policy"), set())

    def test_a_grant_to_public_reaches_the_operator(self):
        unsafe = run(create_table("widget") + tenant_policy("widget", "")
                     + "grant select on public.widget to public;")
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget"})
        safe = run(create_table("widget") + tenant_policy("widget", ""))
        self.assertEqual(subjects(safe, "operator_select_no_policy"), set())

    def test_a_role_membership_hands_the_operator_what_the_app_role_holds(self):
        unsafe = run(create_table("widget") + tenant_policy("widget", "")
                     + "grant select on public.widget to evidgate_app;"
                     + "grant evidgate_app to evidgate_operator;")
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget"})
        safe = run(create_table("widget") + tenant_policy("widget", "")
                   + "grant select on public.widget to evidgate_app;")
        self.assertEqual(subjects(safe, "operator_select_no_policy"), set())

    def test_a_column_level_revoke_removes_no_table_privilege(self):
        unsafe = run(create_table("widget") + tenant_policy("widget", "")
                     + "grant select on public.widget to evidgate_operator;"
                     + "revoke select (id) on public.widget from evidgate_operator;")
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget"})
        safe = run(create_table("widget") + tenant_policy("widget", "")
                   + "grant select on public.widget to evidgate_operator;"
                   + "revoke select on public.widget from evidgate_operator;")
        self.assertEqual(subjects(safe, "operator_select_no_policy"), set())

    def test_a_default_privileges_revoke_reaches_only_later_tables(self):
        defaults = ("alter default privileges for role evidgate_owner in schema public "
                    "grant select on tables to evidgate_operator;")
        revoke = ("alter default privileges for role evidgate_owner in schema public "
                  "revoke select on tables from evidgate_operator;")
        files = {
            "0001_defaults.sql": defaults + create_table("widget") + tenant_policy("widget", ""),
            "0002_revoke.sql": revoke + create_table("gadget") + tenant_policy("gadget", ""),
        }
        self.assertEqual(subjects(run_files(files), "operator_select_no_policy"), {"public.widget"},
                         "the revoke cannot reach the table the earlier default already covered")
        earlier = {"0001_defaults.sql": defaults + create_table("widget") + tenant_policy("widget", "")}
        self.assertEqual(subjects(run_files(earlier), "operator_select_no_policy"), {"public.widget"})

    def test_the_operator_rule_reports_reach_a_role_clause_alone_cannot_see(self):
        unsafe = run(create_table("widget") + tenant_policy("widget", "to current_user")
                     + "grant select on all tables in schema public to evidgate_operator;")
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget"})
        safe = run(create_table("widget") + tenant_policy("widget")
                   + "grant select on all tables in schema public to evidgate_operator;")
        self.assertEqual(safe, [], "without a membership an app-scoped policy yields the operator zero rows")

    def test_a_membership_makes_a_role_scoped_policy_admit_the_operator(self):
        """The one shape that trips this rule on its own: a policy's role list matches by
        membership, so an app-scoped policy reaches an operator that is a member of the app role —
        while no `policy_no_role` statement exists anywhere in the fixture."""
        unsafe = run(create_table("widget") + tenant_policy("widget")
                     + "grant select on public.widget to evidgate_app;"
                     + "grant evidgate_app to evidgate_operator;")
        self.assertEqual(rules(unsafe), {"operator_select_no_policy"})
        self.assertEqual(subjects(unsafe, "operator_select_no_policy"), {"public.widget"})
        safe = run(create_table("widget") + tenant_policy("widget")
                   + "grant select on public.widget to evidgate_app;")
        self.assertEqual(safe, [])

    def test_the_register_says_the_operator_rule_is_narrowed(self):
        text = " ".join((check_rls.__doc__ or "").split())
        self.assertIn("operator_select_no_policy", text)
        self.assertIn("Narrowed on purpose", text)
        self.assertIn("0055", text, "the docstring records that 0055 revokes the operator's SELECT anyway")


if __name__ == "__main__":
    unittest.main()

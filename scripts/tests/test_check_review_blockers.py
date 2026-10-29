"""Line-rule fixtures: D1 policy roles, U8, test skips, casts and scaffold DI.

Every rule fires on the shape it names and stays quiet on the shape that only
looks like it. Diff plumbing, gate-file warnings and the U1 ratchet have their
own files beside this one.
"""
import unittest

from review_blockers_fixtures import RepoTest, blocks, reported, rules_of


class PolicyRoles(RepoTest):
    MIGRATION = "migrations/0100_example.sql"

    def test_a_policy_without_a_role_clause_blocks(self):
        sql = "create policy p on public.widget using (org_id = 1);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_policy_to_public_blocks(self):
        sql = "create policy p on public.widget to public using (org_id = 1);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_quoted_public_role_blocks(self):
        for clause in ('to "public"', "to PUBLIC", 'to "PUBLIC"'):
            with self.subTest(clause=clause):
                sql = f"create policy p on public.widget {clause} using (org_id = 1);\n"
                self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_role_list_containing_public_blocks(self):
        sql = "create policy p on public.widget to evidgate_app, public using (org_id = 1);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_policy_that_names_its_roles_passes(self):
        sql = (
            "create policy p on public.widget for all to evidgate_app\n"
            "  using (org_id = evidgate.current_org_id())\n"
            "  with check (org_id = evidgate.current_org_id());\n"
            "create policy owner_all on public.widget for all to evidgate_owner using (true) with check (true);\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_comments_and_literals_are_not_role_clauses(self):
        sql = (
            "-- create policy commented on public.widget using (true);\n"
            "create policy ok on public.widget to evidgate_app\n"
            "  -- to public is not a clause either\n"
            "  using (note = 'a;b');\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_a_literal_holding_public_does_not_satisfy_the_rule(self):
        sql = "create policy p on public.widget using (note = 'to public');\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_role_clause_inside_the_using_body_is_not_a_role(self):
        without_role = "create policy p on public.widget using (\"to\" > 0);\n"
        with_role = "create policy p on public.widget to evidgate_app using (\"to\" > 0);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, without_role))), {"D1"})
        self.assertEqual(self.scan_change((self.MIGRATION, with_role)), [])

    def test_policy_debt_on_the_base_is_not_reported(self):
        self.scan_change((self.MIGRATION, "create policy old on public.widget using (true);\n"))
        self.assertEqual(rules_of(self.scan_change(("docs/note.md", "unrelated\n"))), set())

    def test_a_quoted_identifier_holding_a_semicolon_is_one_statement(self):
        sql = 'create policy "p;q--not a comment" on public.widget using (true);\n'
        self.assertEqual(reported(self.scan_change((self.MIGRATION, sql)), "D1"), [(self.MIGRATION, 1)])

    def test_a_doubled_quote_does_not_end_the_identifier(self):
        sql = 'create policy "p""on public.widget to public" on public.widget to evidgate_app using (true);\n'
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_an_escaped_quote_in_an_e_string_does_not_end_the_literal(self):
        sql = (
            "create policy p on public.widget to evidgate_app\n"
            "  using (note = E'a\\'b; create policy bad on public.widget using (true)');\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_a_doubled_quote_in_a_plain_string_does_not_end_the_literal(self):
        sql = (
            "create policy p on public.widget to evidgate_app\n"
            "  using (note = 'it''s; create policy bad on public.widget using (true)');\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_a_comment_marker_inside_an_identifier_is_not_a_comment(self):
        sql = 'create policy p on public."widget--x" to evidgate_app using (true);\n'
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_a_table_name_holding_the_word_to_is_not_a_role_clause(self):
        for table in ('public."widget_to_public"', "public.widget_to_public"):
            with self.subTest(table=table):
                sql = f"create policy p on {table} using (true);\n"
                self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_alter_policy_restating_public_roles_blocks(self):
        for clause in ("alter policy p on public.widget to public;", "alter policy p on public.widget to a, PUBLIC;"):
            with self.subTest(clause=clause):
                self.assertEqual(blocks(self.scan_change((self.MIGRATION, clause + "\n"))), {"D1"})

    def test_alter_policy_without_a_role_clause_does_not_fire(self):
        sql = (
            "alter policy p on public.widget using (org_id = evidgate.current_org_id());\n"
            "alter policy p on public.widget to evidgate_app using (true);\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_dynamic_sql_in_a_dollar_body_is_not_inspected(self):
        # Documented limit: procedural SQL would need its own interpreter, so the
        # inner statement is invisible and the rule stays quiet on it.
        sql = "do $$\nbegin\n  execute 'create policy bad on public.widget using (true)';\nend\n$$;\n"
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])


class IgnoreExitCode(RepoTest):
    def test_the_flag_as_an_argument_blocks(self):
        found = self.scan_change(("scripts/verify.sh", 'dotnet test --no-build --ignore-exit-code 8\n'))
        self.assertEqual(blocks(found), {"U8"})

    def test_a_quoted_argument_blocks_too(self):
        found = self.scan_change(("scripts/verify.sh", 'dotnet test "--ignore-exit-code"\n'))
        self.assertEqual(blocks(found), {"U8"})

    def test_a_line_that_tests_for_the_flag_passes(self):
        for line in (
            'grep -q -- "--ignore-exit-code" scripts/verify.sh\n',
            'case "$flag" in --ignore-exit-code) fail=1 ;;\n',
            'if [[ "$args" == *--ignore-exit-code* ]]; then fail=1; fi\n',
            'self.assertTrue(log.contains("--ignore-exit-code"))\n',
            'expect(helpText.includes("--ignore-exit-code")).toBe(false);\n',
        ):
            with self.subTest(line=line):
                # scripts/verify.sh is a gate file, so a WARN gate-file is expected; the rule under test must not fire.
                self.assertEqual(blocks(self.scan_change(("scripts/verify.sh", line))), set())

    def test_docs_and_shell_comments_are_out_of_scope(self):
        self.assertEqual(rules_of(self.scan_change(("docs/engineering/gates.md", "never pass `--ignore-exit-code`\n"))), set())
        self.assertEqual(blocks(self.scan_change(("scripts/verify.sh", "# never pass --ignore-exit-code\n"))), set())

    def test_a_refusal_word_elsewhere_on_the_line_does_not_excuse_the_flag(self):
        for line in (
            "dotnet test --ignore-exit-code 8  # grep\n",
            "dotnet test --ignore-exit-code 8  # case\n",
            'logger.info("inspecting"); dotnet test --ignore-exit-code 8\n',
        ):
            with self.subTest(line=line):
                self.assertEqual(blocks(self.scan_change(("scripts/verify.sh", line))), {"U8"})

    def test_a_posix_test_expression_that_refuses_the_flag_passes(self):
        for line in (
            '[ "$flag" = "--ignore-exit-code" ] && fail=1\n',
            "if [ $# -gt 0 ] && [ -z \"${1##*--ignore-exit-code*}\" ]; then fail=1; fi\n",
            'case "$flag" in --ignore-exit-code|reset) fail=1 ;;\n',
            "--ignore-exit-code) fail=1 ;;\n",
        ):
            with self.subTest(line=line):
                self.assertEqual(blocks(self.scan_change(("scripts/verify.sh", line))), set())


class TestSkips(RepoTest):
    def test_attribute_and_runtime_skips_block(self):
        for path, line in (
            ("tests/ExampleTests.cs", '[Fact(Skip = "flaky")]\n'),
            ("tests/DockerTests.cs", '[DockerFact(Skip = "no daemon")]\n'),
            ("tests/EnvTests.cs", '[Fact(SkipUnless = "EVIDGATE_DB")]\n'),
            ("apps/web/src/app/x.test.ts", 'it.skip("later", () => {});\n'),
            ("apps/web/src/app/x.test.ts", 'xit("later", () => {});\n'),
            ("apps/web/src/app/x.spec.ts", 'test.fixme("later", () => {});\n'),
        ):
            with self.subTest(line=line):
                self.assertEqual(blocks(self.scan_change((path, line))), {"skip"})

    def test_runtime_skip_helpers_block(self):
        for line in ('Skip.If(true, "not here");\n', 'Assert.Skip("no docker");\n'):
            with self.subTest(line=line):
                self.assertEqual(blocks(self.scan_change(("tests/ExampleTests.cs", line))), {"skip"})

    def test_paging_skip_is_not_a_test_skip(self):
        self.assertEqual(self.scan_change(("tests/PagingTests.cs", "options.Skip = 5;\n")), [])

    def test_an_attribute_split_across_lines_blocks_on_its_skip_line(self):
        text = '[Fact(\n    Skip = "flaky")]\npublic void T() { }\n'
        self.assertEqual(reported(self.scan_change(("tests/SplitTests.cs", text)), "skip"), [("tests/SplitTests.cs", 2)])

    def test_a_skip_on_a_comma_attribute_list_blocks(self):
        text = '[Trait("Category", "slow"), Fact(Skip = "no daemon")]\n'
        self.assertEqual(blocks(self.scan_change(("tests/TraitTests.cs", text))), {"skip"})

    def test_a_skip_assignment_without_an_attribute_above_it_does_not_block(self):
        text = "var query = new Paged\n(\n    Skip = 5);\n"
        self.assertEqual(self.scan_change(("tests/PagingTests.cs", text)), [])

    def test_a_skip_outside_a_test_file_is_not_scanned(self):
        self.assertEqual(self.scan_change(("src/Thing.cs", '[Fact(Skip = "x")]\n')), [])


class TypeCasts(RepoTest):
    def test_a_double_cast_blocks(self):
        found = self.scan_change(("apps/web/src/app/x.ts", "const v = f() as unknown as Signal<number>;\n"))
        self.assertEqual(blocks(found), {"ts-cast"})

    def test_a_single_cast_passes(self):
        self.assertEqual(self.scan_change(("apps/web/src/app/x.ts", "const v = f() as Signal<number>;\n")), [])


class Scaffolding(RepoTest):
    PATH = "src/Evidgate.Api/Composition.cs"

    def test_a_registration_on_one_line_warns(self):
        found = self.scan_change((self.PATH, "services.AddSingleton<IHealingLlmProvider, MockHealingLlmProvider>();\n"))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "U4")])

    def test_a_registration_across_two_lines_warns_once(self):
        text = "services.AddSingleton<IHealingLlmProvider>(\n    new MockHealingLlmProvider());\n"
        found = self.scan_change((self.PATH, text))
        self.assertEqual([finding.rule for finding in found], ["U4"])
        self.assertEqual(found[0].line, 2)

    def test_a_real_registration_passes(self):
        self.assertEqual(self.scan_change((self.PATH, "services.AddSingleton<IClock, SystemClock>();\n")), [])


if __name__ == "__main__":
    unittest.main()

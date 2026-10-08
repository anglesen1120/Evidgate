# fixtures/ci-recipes/

Customer CI workflow fixtures for story 11.2.1 (AC1; FR-11-2, 00 §5.7 S3), checked by `scripts/lint-ci-recipe.sh`. Every file is synthetic (handcrafted for 11.2.1-T1): no real repository, account, token or customer data. Credentials appear only as `${{ secrets.NAME }}` expressions or obviously fake names, never as token-shaped literals.

This is _not_ a `<domain>/<name>/` fixture corpus: no directory here carries a `meta.json` or an `input.*` file, so the .NET and TypeScript fixture loaders skip it.

## Layout

```
fixtures/ci-recipes/
  github-fork-safe.yml   # the published fork-safe recipe (docs/security/customer-ci-recipe.md); lints clean
  negative/              # one fixture per FR-11-2 rule; each breaks exactly that rule and nothing else
  bypass/                # bypass attempts: the same violations hidden behind YAML or expression spellings
```

## `# expect:` convention

The first line of every file under `negative/` and `bypass/` is `# expect: <rule-id>`, where `<rule-id>` is one of `R1-no-pull-request-target`, `R2-sha-pinned`, `R3-token-permissions`, `R4-add-mask`, `R5-no-long-lived-credential` or `yaml-parse`. The second comment line says what the fixture hides.

`scripts/lint-ci-recipe.sh --self-test` runs the real lint on every file and prints one PASS/FAIL line each:

- `github-fork-safe.yml` must exit 0;
- each `negative/` fixture must exit 1 reporting its expected rule and no other rule;
- each `bypass/` fixture must exit 1 reporting its expected rule (another rule may fire as well).

## Bypass attempts covered

- `b01` an anchored scalar resolved through `uses: *ref`; `b02` a flow-style step `{ uses: … }` (R2);
- `b03` a quoted `'on':` key; `b09` the list form `on: [push, workflow_run]` (R1);
- `b04` `secrets['NAME']`; `b05` `toJSON(secrets)`; `b06` `secrets: inherit`; `b10` `secrets.NAME` in an `if:` condition without `${{ }}`; `b14` a `--ingest-token` CLI argument (R5);
- `b07` `permissions: write-all`; `b08` a merge key `<<:` injecting a job's write scope into the workflow default (R3);
- `b13` a heredoc writing a derived value into `$GITHUB_ENV` (R4);
- `b11` a second YAML document; `b12` a duplicate `on:` key (`yaml-parse`: ambiguous between parsers).

All files stay in prettier's own YAML style (single quotes, spaced flow braces) except `b12-duplicate-on-key.yml`: prettier's YAML parser refuses a duplicate key too, so `.prettierignore` lists that one file.

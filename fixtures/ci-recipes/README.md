# fixtures/ci-recipes/

Customer CI workflow fixtures for story 11.2.1 (AC1; FR-11-2, 00 §5.7 S3), checked by `scripts/lint-ci-recipe.sh`. Every file is synthetic (handcrafted for 11.2.1-T1): no real repository, account, token or customer data. Credentials appear only as `${{ secrets.NAME }}` expressions or obviously fake names; the only token-shaped values are the made-up test strings of `bypass/b19` (an `egt_` literal) and `bypass/b38` (`not-a-real-token-value`), which exist to be refused.

This is _not_ a `<domain>/<name>/` fixture corpus: no directory here carries a `meta.json` or an `input.*` file, so the .NET and TypeScript fixture loaders skip it.

## Layout

```
fixtures/ci-recipes/
  github-fork-safe.yml   # the published fork-safe recipe (docs/security/customer-ci-recipe.md); lints clean
  negative/              # one or more fixtures per FR-11-2 rule; each breaks exactly its rule and nothing else
  bypass/                # bypass attempts: the same violations hidden behind YAML or expression spellings
```

## `# expect:` convention

The first line of every file under `negative/` and `bypass/` is `# expect: <rule-id>`, where `<rule-id>` is one of `R1-no-pull-request-target`, `R2-sha-pinned`, `R3-token-permissions`, `R4-add-mask`, `R5-no-long-lived-credential` or `yaml-parse`. The second comment line says what the fixture hides.

`scripts/lint-ci-recipe.sh --self-test` runs the real lint on every file and prints one PASS/FAIL line each:

- `github-fork-safe.yml` must exit 0;
- each `negative/` fixture must exit 1 reporting its expected rule and no other rule;
- each `bypass/` fixture must exit 1 reporting its expected rule (another rule may fire as well).

## Negative fixtures

`negative/` holds one fixture per rule (`r1`–`r5`), plus the R3 job-level cases added by 11.6.1-T2: a job that grants `id-token: write` while the workflow runs on `pull_request` — directly (`r3-id-token-job-on-pull-request.yml`), through a YAML alias (`r3-id-token-job-alias-on-pull-request.yml`) or as `write-all` (`r3-write-all-job-on-pull-request.yml`) — and two guards the lint refuses, a `!= 'pull_request'` negation (`r3-id-token-guard-negation.yml`) and a `|| github.event_name == 'pull_request'` disjunct (`r3-id-token-guard-pr-disjunct.yml`).

## Bypass attempts covered

- `b01` an anchored scalar resolved through `uses: *ref`; `b02` a flow-style step `{ uses: … }` (R2);
- `b03` a quoted `'on':` key; `b09` the list form `on: [push, workflow_run]` (R1);
- `b04` `secrets['NAME']`; `b05` `toJSON(secrets)`; `b06` `secrets: inherit`; `b10` `secrets.NAME` in an `if:` condition without `${{ }}`; `b14` a `--ingest-token` CLI argument (R5);
- `b07` `permissions: write-all`; `b08` a merge key `<<:` injecting a job's write scope into the workflow default (R3);
- `b13` a heredoc writing a derived value into `$GITHUB_ENV` (R4);
- `b15` a mask on the same line as the write; `b16` a mask inside a comment; `b24` a mask inside another echo string (R4: a mask counts only as its own earlier command);
- `b28` a mask redirected to `/dev/null`; `b29` a partial mask `::add-mask::pre-$V`; `b30` a mask after `&&`; `b31` a mask line inside another heredoc body; `b33` a masked variable reassigned before the write; `b35` `::stop-commands::` before the mask; `b39` a quoted `<<EOF` that is not a heredoc; `b40` a mask inside a multi-line `if` body; `b41` `V+=…` after the mask; `b43` a quote-split `::stop-commands::`; `b45` an arithmetic shift `(( x = 1<<N ))` that is not a heredoc; `b46` `read V`, `b47` `while read V`, `b48` `export V=…` and `b49` `coproc V { …; }` after the mask (R4: a mask counts only in its exact, unconditional form, until the variable may be rebound);
- `b17` a `pwsh` run step; `b18` `defaults.run.shell: python`; `b34` `runs-on: ${{ matrix.os }}` without `shell:` (R4: only bash/sh bodies are analysable);
- `b21` `cp` into `$GITHUB_ENV`; `b22` an indirect target `F=$GITHUB_ENV`; `b23` the `${{ github.env }}` context; `b27` an env-prefix assignment before the write; `b32` indirect expansion `${!F}`; `b42` a target from `$(printenv GITHUB_ENV)` (R4);
- `b19` a committed `egt_` ingest-token literal; `b20` `vars.*` in a credential `with:` input; `b25` `vars.*` in a credential `env:` key; `b26` a short `-t` flag on `evidgate`; `b36` a `}}` inside an expression string literal; `b37` an underscore flag `--ingest_token`; `b38` a literal value under a credential-named `env:` key; `b44` the same under a credential-named `with:` input (R5);
- `b11` a second YAML document; `b12` a duplicate `on:` key (`yaml-parse`: ambiguous between parsers).

All files stay in prettier's own YAML style (single quotes, spaced flow braces) except `b12-duplicate-on-key.yml`: prettier's YAML parser refuses a duplicate key too, so `.prettierignore` lists that one file.

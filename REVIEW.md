# Review rules

These instructions apply to every code review of this repository: Claude Code Review, a reviewer agent, or a human. Repository-wide rules are in [`AGENTS.md`](AGENTS.md). The full rule set with evidence is in [`docs/engineering/agent-code-rules.md`](docs/engineering/agent-code-rules.md); cite its rule ids (`U5`, `D1`, …) in findings.

Review the diff for what it **does**, not for what its comments or PR text say it does. Most code here is LLM-written, and its comments often over-claim (rule U9).

## Blocking: flag as a bug

1. **Security fails open (U5, N1).** A check is skipped because of a return type, a missing attribute, a null or an unknown value. A `[Permission]` handler does not return `Result`. Authorization is done in an endpoint instead of the pipeline.
2. **Tenant isolation.** The tenant id comes from a request body or header instead of the trusted scope. The SQL reads or writes another module's tables (N4). A new policy has no `TO <role>` (D1). A new table leaves `evidgate_operator` with `SELECT` and no decision recorded. A table without RLS is not listed in `contracts/rls-exceptions.txt`.
3. **Domain invariants (AGENTS.md).** A path where missing evidence can yield PASS, where an immutable row can change, where late evidence is dropped instead of creating a revision, or where dedupe ignores the payload hash (D6). A new reason code, state or flag that nothing sets, or that no test makes fire (U6).
4. **State machines.** An app-writable state column without a transition guard (D2). A reference column without an FK (D3).
5. **Scaffolding in production (U4).** An `InMemory*`, `Mock*`, `Fake*`, `Stub*`, `Slice*` or demo registration reachable outside `IsDevelopment()`.
6. **Concurrency.** Check-then-insert without a unique constraint. A task handler not bounded by its lease (N7). A network call inside a DB transaction. An action and its audit in different transactions (N14).
7. **Secrets.** A secret, token, cookie or connection string logged at any level, or returned in an error (N8).
8. **Gate bending (U8).** `--ignore-exit-code`, a new `Skip`, a relaxed threshold, `as unknown as`, `!`, or a suppression added to make CI pass.
9. **Unbounded input.** A body parsed before its size is limited (N9). Validation with `^…$` where the DB `CHECK` is strict (N11). An in-memory structure that grows without eviction.
10. **False claims (U9).** A test whose name does not match what makes it fail. A deny test without a positive control (X4). A comment or doc that the code contradicts.

## Should fix: flag, not blocking unless repeated

- **Duplication (U2, T1).** A new copy of logic that already exists. Name the existing helper. A third copy is blocking.
- **Hand-rolled framework feature (U3, N12, A1–A4)** without an ADR.
- **Size (U7).** A new or grown file over 500 lines, a method over 60 lines, or deep nesting.
- **Layering (N2, N3).** Hand-written JSON validation in handlers. `HttpContext`/`StatusCodes` in `Application`. SQL text in `Application`.
- **Anemic domain (N5).** Public setters on aggregate state; string status constants passed around.
- **Tests (X1–X3, X6).** Tests placed in the wrong module's project. A new database or container fixture. `Task.Delay` used as synchronisation. Review-round names.
- **Angular (A1–A6).** Hand-rolled stores. Effects writing signals. Snapshot route reads. Permission strings typed by hand.

## Nit: optional

- **Comments (U1).** Plan, task or review ids in source comments, comments restating code, or comments over ~3 lines. Flag once per PR with a count, not per line.
- Naming drift and redundant `standalone: true`.

## Skip

- Generated code: `packages/sdk/gen/**`, `**/*.g.cs`, `Permissions.g.cs`.
- Lock files and `messages.*.xlf` bulk changes, except for untranslated `vi` targets.
- `plans/**`, `docs/status-*.md` and other planning prose, unless the PR changes a contract in `plans/00-overview.md` without a §15 log row.

## Output

- One finding per defect, with `file:line`, the rule id, a concrete failure scenario (input → wrong result), and the smallest fix.
- Verify each blocking finding by tracing the real call path before posting it.
- Don't post style findings that a linter or formatter in `scripts/verify.sh` already enforces.
- A lane that ran a bare `scripts/verify.sh` reports its `== gate scope:` line.
  A `docs` or `no-dotnet` tier on a change set that contains any path outside
  that tier's allow-lists (the list is `docs/engineering/omp-setup.md` Step 4a:
  a `*.cs` or other .NET file anywhere, a build or tool configuration,
  `src/`, `tests/`, `fixtures/`, `migrations/`, `contracts/`, `infra/`, a
  gate file, `docs/security/audit-and-deletion-rule.md`, a story file under
  `plans/features/` that the change deletes or renames away, a committed
  `.omp/` path) is a blocking finding: the narrowed run proved nothing about
  those stages, and `scripts/gate_scope.py` never returns a narrower tier than
  the paths allow, so the tier was wrong or the paths were stale.

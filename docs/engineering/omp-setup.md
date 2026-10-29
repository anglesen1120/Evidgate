# omp setup: wire the code rules into the agent harness

**Audience:** the omp coordinator (or the owner) right after pulling `develop`.
**Trigger:** the owner says "read `docs/engineering/omp-setup.md` and configure".

The binding rules for coding agents live in [`agent-code-rules.md`](agent-code-rules.md), the review rules in [`../../REVIEW.md`](../../REVIEW.md), and the prioritised fix register in [`../../plans/features/REVIEW-2026-10-29.md`](../../plans/features/REVIEW-2026-10-29.md). omp discovers the root `AGENTS.md` by itself ✅ ([oh-my-pi README](https://github.com/can1357/oh-my-pi): "gitignore + AGENTS.md discovery in one pass"). That file carries only a summary. The task and review prompts under `.omp/` are not in git, so this file says what to add to them.

Every step is idempotent. Before inserting a block, search the target file for the marker line `<!-- evidgate-code-rules v1 -->` followed by that block's first line (one file, such as `PROJECT.md`, can hold several blocks), and check that the block's last line is there too. If both are there, skip the insertion; if only part of the block is there, replace it with the whole block. Change only the files named below, and report a diff of each change to the owner at the end.

## Step 1: Find the prompt files

1. Run `ls -R .omp/` from the repo root of the main checkout. The `.omp/` folder is untracked and exists only there; worktrees have none.
2. Identify these files and list them in your report:
   - **coder templates:** prompt templates used to hand a task to a coding agent (for example under `.omp/coordinator/prompts/`, including `held/`);
   - **reviewer templates:** prompt templates for review agents (the ones that score `acceptance`, `reviews_cover_final`, `clean_of_blockers`, `handed_over_not_dropped`);
   - **gate spec:** the gate/merge rules, `.omp/coordinator/PROJECT.md` §Gate;
   - **agent definitions:** files under `.omp/agents/`, if present.
3. If a category has no file, say so in the report. Do not invent a new file layout.

## Step 2: Coder templates

Append this block to every coder template outside `prompts/archive/` (in the panel prompts: just before `## 11. Final report`), after the task description and before any output-format section:

```markdown
<!-- evidgate-code-rules v1 -->
## Code rules (binding)
- Before writing code, read `docs/engineering/agent-code-rules.md` §0 and §1, plus the section for each layer you touch: §2 .NET, §3 SQL, §4 TypeScript, §5 Angular, §6 tests. Search §7 (helper catalogue) and `rg` before writing any helper.
- Priority: while `plans/features/REVIEW-2026-10-29.md` has a P0 row in state `open`, P0 rows come before this task unless the owner says otherwise. A P1 row comes before the task named in its "Must precede" cell. When you fix a row, set its State cell to `fixed (PR, commit)`.
- Forbidden:
  - plan/task/review ids in source comments;
  - a second copy of an existing helper;
  - `InMemory*`/`Mock*`/`Slice*` registrations outside `IsDevelopment()`;
  - a handler with `[Permission]` that does not return `Result`;
  - an RLS policy without `TO <role>`;
  - `--ignore-exit-code`, a new `Skip`, `as unknown as`, or logging a secret.
- Final report must contain:
  1. the rule ids you applied (for example `U5`, `N1`, `D1`);
  2. every `scripts/verify.sh` stage you ran, with its exit code;
  3. what you could not run or verify;
  4. any rule you deliberately did not follow, and why.
```

## Step 3: Reviewer templates

Append this block to every reviewer template. If the coordinator has no reviewer template (Evidgate's does not: the reviewers are briefed from `PROJECT.md`), append it at the end of `PROJECT.md` §Merge check questions instead:

```markdown
<!-- evidgate-code-rules v1 -->
## Review rules (binding)
- Review against `REVIEW.md` at the repo root. Cite the rule id from `docs/engineering/agent-code-rules.md`, `file:line`, and a concrete failure scenario for each finding.
- Any item in REVIEW.md "Blocking" that is present in the diff caps `clean_of_blockers` below 0.8 and is a send-back.
- REVIEW.md "Should fix" items are findings, not blockers. A third copy of duplicated logic (U2) counts as blocking.
- Judge the code, not the comments or the PR text. Comments that claim "idempotent", "safe" or "atomic" without a test are a finding (U9).
```

## Step 4: Gate spec

In the gate section of `PROJECT.md`, add one line under the merge conditions:

```markdown
<!-- evidgate-code-rules v1 -->
- A PR that fixes a row of `plans/features/REVIEW-2026-10-29.md` updates that row's State cell in the same PR. A PR that adds a rule violation listed in `REVIEW.md` "Blocking" is not merged under coordinator review.
```

## Step 5: Queue order

If the coordinator keeps a refill or queue file (for example `.omp/coordinator/prompts/held/README.md` or `next-wave-queue.md`), put the P0 rows CR-01…CR-06 of `plans/features/REVIEW-2026-10-29.md` at the head of the backend lane. That matches the owner priority recorded in `STATUS.md` §1 on 2026-10-29. Use the register's sizes and skip rows whose State is already fixed. While CR-03 is open, the next free migration number (`0055` on 2026-10-30) is reserved for it. CR-03 and CR-06 also carry the D1 and U8 gates of `agent-code-rules.md` §8. `GATE-1` landed on 2026-10-30 as task `01.2.1-T6`: `scripts/check-review-blockers.py` runs as the `check-review-blockers` stage of `scripts/verify.sh`, wired into `ci.yml` beside `check-rls`, with its fixtures in `scripts/tests/test_check_review_blockers.py` and the U1 ratchet baseline in `scripts/check-review-blockers.baseline.json`. It touched `scripts/verify.sh` like CR-06, so it was queued after it.

## Step 5a: Coordinator rules in PROJECT.md

The kit template `panel-prompt.md` under `~/.omp/agent/managed-skills/tern-parallel-wave-coordinator/kit/` is shared by every project, so Evidgate rules must not go there. Without the lines below, every prompt the coordinator writes after this setup misses the Step 2 block. Append this block at the end of `PROJECT.md` §Active rules:

```markdown
<!-- evidgate-code-rules v1 -->
- Code rules in every new panel prompt (owner 2026-10-30): the kit template `panel-prompt.md` is project-agnostic, so when writing a prompt copy the Step 2 block of `docs/engineering/omp-setup.md` (marker `<!-- evidgate-code-rules v1 -->`) verbatim just before `## 11. Final report`.
- Reviewer brief (owner 2026-10-30): §5 of every new panel prompt says "every QA, reviewer and security-reviewer subagent prompt includes `REVIEW.md` and `docs/engineering/agent-code-rules.md`, and each finding cites the rule id, `file:line` and a concrete failure scenario".
```

Then add this line to the Step 4 block in §Gate. The coordinator's merge checks (kit §6.1) only check process, and jev reads the panel's report, not the diff; this scan reads the diff:

```markdown
- Blocker scan (owner 2026-10-30, hard check after kit §6.1 items 1-2, before jev): `git fetch origin`, then extract develop's own copies into a clean directory and run them from the main checkout — `rm -rf /tmp/crb-dev && mkdir -p /tmp/crb-dev && git archive origin/develop -- scripts/check-review-blockers.py scripts/check-review-blockers.baseline.json $(git cat-file -e origin/develop:scripts/review_blockers/__init__.py 2>/dev/null && echo scripts/review_blockers) | tar -x -C /tmp/crb-dev && python3 /tmp/crb-dev/scripts/check-review-blockers.py origin/develop <head-sha> && python3 /tmp/crb-dev/scripts/check-review-blockers.py --baseline /tmp/crb-dev/scripts/check-review-blockers.baseline.json --u1-ratchet <head-sha>`. Extract and run the copies: piping a file into python would mask a missing develop copy, an extraction that left the `scripts/review_blockers` package behind would import the checkout's own rules, and a PR must not weaken the gate that judges it. The `git cat-file` test is there because a develop that predates the split has only the self-contained entry file, which `git archive` refuses to miss. Exit 2 = the scan did not run = failed check. Exit 1 (any `BLOCK` line: D1 `CREATE`/`ALTER POLICY` without `TO <role>` or `TO public`, U8 `--ignore-exit-code` used as an argument, a new test skip, `as unknown as` anywhere, or a U1 count above develop's baseline) = no jev, send the lines to the panel as `Coordinator review of PR #N (not merged yet)`. `WARN U4` = open the line, block unless it sits inside `IsDevelopment()`. `WARN gate-file` = block unless the task file names that file. `WARN U1` = note in the merge note, not a block. This check overrides 6c: a `BLOCK` still present after the one send-back goes to the owner, not to a debt row. A line the coordinator proves is a false positive (regex hit that is not the pattern) is waived in the merge note with the line and the reason.
```

`scripts/check-review-blockers.py` is the entry point (`scripts/review_blockers/` holds the rules) and the specification; the table below is its human-readable summary, and where the two disagree the script wins. It reads `git diff base...head` and checks only the lines the PR adds, so debt already on `develop` never fails a PR (and the scan cannot prove that something was removed). The `check-review-blockers` stage of `scripts/verify.sh` runs the scan with `EVIDGATE_REVIEW_BASE` (CI sets the merge commit's first parent `HEAD^1` on a pull request, with the payload base sha only when the checkout has no second parent and then with a `::warning::`; the ref a push started from on a push) or, unset, the first of `origin/develop`, `develop`, `origin/main`, `main` that resolves, then the U1 ratchet over the whole head tree against `scripts/check-review-blockers.baseline.json`, and the fixtures in `scripts/tests/test_*blockers*.py` (line rules, diff transport, ratchet) with the shared `review_blockers_fixtures.py` helper, which the stage's `test_*blockers*.py` glob never matches; `--u1-count <ref>` prints the count a lowered baseline should carry. On a pull request CI does not trust the checkout's copy: a second step archives the base sha's `scripts/check-review-blockers.py`, its `scripts/review_blockers` package when that base has one, and `…baseline.json` into a clean `$RUNNER_TEMP` directory and runs that copy for the ratchet and, on a develop landing, for the diff scan (`--baseline <file>`), so editing a rule or raising the ceiling in the pull request's own copy cannot pass it — the payload base sha names that trusted copy because it must be a commit of the base branch, while the scan range is the merge commit's first parent, so a branch that merged develop cannot re-scan base-side changes as its own; it falls back to the pull request's copy, with a `::warning::`, only when the base revision has no scanner entry file at all (the bootstrap, i.e. the change that introduced the gate), and a base that has the scanner but no baseline fails the step instead of borrowing the pull request's number. A change to the scanner or its baseline therefore shows up in that diff as a `WARN gate-file` line the coordinator weighs against the task file, exactly as for any other gate file, while the rules and the ceiling the change set is judged by stay the base's; the same diff cannot let the change set raise them. The diff scan runs only for a landing on develop — a pull request to develop, or a push to develop — while a pull request to main or a push to main runs with `EVIDGATE_REVIEW_DIFF=off`, so the ratchet runs alone: develop's own debt that the scan tolerates (the `0053`/`0054` D1 policies) is not a promotion's change set, and scanning it would fail every promotion for it. A missing base commit (a shallow clone) fails the step with its sha in the message, and the bootstrap warning above disappears as soon as `develop` carries the scanner. A pull request runs its own `ci.yml` and `scripts/verify.sh` as well, so the base copy protects the scanner and the baseline, not the workflow: the workflow's copy is the coordinator's `origin/develop` check plus the `WARN gate-file` line, never this step. The retired `.omp/coordinator/check-review-blockers.py` stop-gap must not be run once the repo copy is on `develop`.

|Rule|Files|Pattern on an added line|Level|
|---|---|---|---|
|D1|`migrations/*.sql` the change set touches (whole head file)|a `CREATE POLICY`, or an `ALTER POLICY` that restates its roles, whose role list (the `TO` clause outside its parentheses) is missing or contains `public`, quoted or not; comments and quoted text removed first; static statements only — a policy built by `EXECUTE` inside `DO $$ … $$` is not inspected and stays debt|BLOCK|
|U8|every path except `docs/`, `plans/`, `*.md`; not `#` comment lines|any `--ignore-exit-code`, quoted or not, except on a line that tests for it (`grep`, `rg`, `case`, `[[ ]]`, `in <name>:`, `.contains(`/`.includes(`)|BLOCK|
|skip|`tests/`, `*.spec.ts`, `*.test.ts`, `/e2e/`|`Skip`/`SkipUnless`/`SkipWhen` `=` inside a `[…Fact(`/`[…Theory(` attribute (custom names too), `Skip.If(`, `Assert.Skip(`, `it/test/describe.skip`/`.skipIf`/`.fixme`, `xit(`/`xdescribe(`/`xtest(`|BLOCK|
|ts-cast|`*.ts`, `*.tsx`, `*.mts`, `*.cts`|`as unknown as`|BLOCK|
|U4|`src/`|`(Try)Add{Singleton,Scoped,Transient,HostedService}` with an `InMemory`/`Mock`/`Fake`/`Stub`/`Slice`/`Demo`/`Noop` type, on that line or across two lines|WARN|
|gate-file|`scripts/verify.sh`, `scripts/check[-_]<name>`, `scripts/review_blockers/<name>.py`, `scripts/tests/(test_)?(check_)?review_blockers<rest>.py` (the three test files and the `review_blockers_fixtures.py` helper), `.github/workflows/<name>`|any change|WARN|
|U1|`:(top,glob)src/**/*.cs`, `:(top,glob)apps/web/src/**/*.ts`, `:(top,glob)packages/*/src/**/*.ts` (git pathspec globs; zero matched files exits 2)|a comment line carrying `NN.E.S-Tn`, `CR-nn` or `D-Snnn`|one WARN with the count in a diff scan; `--u1-ratchet` counts the whole head tree and BLOCKs above the baseline, printing an advisory (never a failure) when the count falls below it|

Exit codes: 1 on any BLOCK or a U1 count above the baseline, 0 otherwise, 2 when the scan could not run — a missing git binary, a failing git call, an unreadable baseline, a crash, or a U1 pathspec that matches no tracked file, so a gate failure is never mistaken for a scan failure.

The scanner entry, its `scripts/review_blockers` package, its fixture files and its baseline are exempt from the line rules: they carry the patterns by design. Every edit to them is still a `gate-file` warning, because an edit to the gate must always be visible and raising the baseline is the one way to disarm the ratchet from inside the change set it judges; rename detection is off for the changed-path list, so a gate file moved away still warns on its deleted old path. The ratchet reads the baseline of the working tree it runs in, so the coordinator's run from the main checkout (on `develop`) judges a PR's baseline edit against develop's number, not the PR's.

Also fix the next free migration number in `PROJECT.md` §Shared files when it is stale (`ls migrations`).

## Step 6: An omp rule (TTSR reminder)

omp loads project rules from `<cwd>/.omp/rules/*.md` when the cwd's `.omp/` directory is not empty ✅ (omp docs, `rulebook-matching-pipeline.md` §native provider). Panels start omp in the main checkout, so the rule reaches them. Create `.omp/rules/evidgate-code-rules.md` with `interruptMode: never` (a reminder, never an interrupt), a `scope` of `tool:edit`/`tool:write` on the code, SQL, shell, YAML and project files, and a `condition` regex for the Step 5a patterns. Its body names the rule ids and points to `docs/engineering/agent-code-rules.md`, `REVIEW.md` and the Step 5a table; do not copy the rules into it, so there is one source of truth.

## Step 7: Report to the owner

Write a short report:
- the files changed, with a diff each;
- the files you expected but did not find;
- blocks skipped because the marker was already present.

Do not commit anything under `.omp/`: it stays local.

## Maintenance

When `agent-code-rules.md`, `REVIEW.md` or the register changes in a way that changes the blocks above, edit this file and bump the marker of **only** the blocks whose text changed, since each block carries its own version and an unchanged block must keep its old marker (a re-run then skips it instead of rewriting it). Re-running the setup then replaces each superseded block with the new one: delete from the old marker line to the next blank line or heading, then insert the new block at the same place. The prose outside the blocks (Step 5's queue, Step 5a's commands and the spec table) is not marked: say explicitly which older line it replaces, as the 2026-10-30 blocker-scan rewrite does.

After each wave, move the prompts of released sessions (SESSION-CLAIMS `Released`) from `.omp/coordinator/prompts/` and `held/` into `prompts/archive/` (`archive/held/` for held ones). Step 2 skips `archive/`, so a re-run touches only live prompts.

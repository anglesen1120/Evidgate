# omp setup: wire the code rules into the agent harness

**Audience:** the omp coordinator (or the owner) right after pulling `develop`.
**Trigger:** the owner says "read `docs/engineering/omp-setup.md` and configure".

The binding rules for coding agents live in [`agent-code-rules.md`](agent-code-rules.md), the review rules in [`../../REVIEW.md`](../../REVIEW.md), and the prioritised fix register in [`../../plans/features/REVIEW-2026-10-29.md`](../../plans/features/REVIEW-2026-10-29.md). omp discovers the root `AGENTS.md` by itself ✅ ([oh-my-pi README](https://github.com/can1357/oh-my-pi): "gitignore + AGENTS.md discovery in one pass"). That file carries only a summary. The task and review prompts under `.omp/` are not in git, so this file says what to add to them.

Every step is idempotent. Before inserting a block, search the target file for the marker line `<!-- evidgate-code-rules v1 -->` followed by that block's first line (one file, such as `PROJECT.md`, can hold several blocks). If that pair is there, skip the insertion. Change only the files named below, and report a diff of each change to the owner at the end.

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

Append this block to every reviewer template:

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

If the coordinator keeps a refill or queue file (for example `.omp/coordinator/prompts/held/README.md` or `next-wave-queue.md`), put the P0 rows CR-01…CR-06 of `plans/features/REVIEW-2026-10-29.md` at the head of the backend lane. That matches the owner priority recorded in `STATUS.md` §1 on 2026-10-29. Use the register's sizes. The next free migration `0055` is reserved for CR-03. CR-03 and CR-06 also carry the D1 and U8 gates of `agent-code-rules.md` §8. After CR-06, queue `GATE-1`: the repo gate `scripts/check-review-blockers.py` (D1, U8, new `Skip`, `as unknown as` fail; U4 and gate-file changes reported; U1 comment ids in ratchet mode) as a `verify.sh` stage wired into `ci.yml`. It edits `scripts/verify.sh` like CR-06, so it runs after it, and it needs an owner-approved task row (suggested 01.2.1-T6).

## Step 5a: Coordinator rules in PROJECT.md

The kit template `panel-prompt.md` under `~/.omp/agent/managed-skills/tern-parallel-wave-coordinator/kit/` is shared by every project, so Evidgate rules must not go there. Without the lines below, every prompt the coordinator writes after this setup misses the Step 2 block. Append this block at the end of `PROJECT.md` §Active rules:

```markdown
<!-- evidgate-code-rules v1 -->
- Code rules in every new panel prompt (owner 2026-10-30): the kit template `panel-prompt.md` is project-agnostic, so when writing a prompt copy the Step 2 block of `docs/engineering/omp-setup.md` (marker `<!-- evidgate-code-rules v1 -->`) verbatim just before `## 11. Final report`.
- Reviewer brief (owner 2026-10-30): §5 of every new panel prompt says "every QA, reviewer and security-reviewer subagent prompt includes `REVIEW.md` and `docs/engineering/agent-code-rules.md`, and each finding cites the rule id, `file:line` and a concrete failure scenario".
```

Then add this line to the Step 4 block in §Gate. The coordinator's merge checks (kit §6.1) only check process, and jev reads the panel's report, not the diff; this scan reads the diff:

```markdown
- Blocker scan (owner 2026-10-30, hard check after kit §6.1 items 1-2, before jev): `python3 .omp/coordinator/check-review-blockers.py origin/develop <head-sha>` from the main checkout. Exit 1 (any `BLOCK` line: D1 policy without `TO`, U8 `--ignore-exit-code`, a new `Skip`, `as unknown as` outside tests) = no jev, send the lines to the panel as `Coordinator review of PR #N (not merged yet)`. `WARN U4` = open the line, block unless it sits inside `IsDevelopment()`. `WARN gate-file` = block unless the task file names that file. `WARN U1`/`ts-cast` in tests = note in the merge note, not a block. When the repo gate `scripts/check-review-blockers.*` lands, run the origin/develop copy of it (`git show origin/develop:<path>`) instead and delete the `.omp` script.
```

`.omp/coordinator/check-review-blockers.py` is a coordinator-local stop-gap (scans only the lines a PR adds; exit 1 on a block). The queue row `GATE-1` (Step 5) ports it into the repo as a `verify.sh` stage with tests, after CR-06; from then on the repo copy is the only pattern list. If the `.omp` script is missing, write it from the `GATE-1` row's rule list.

Also fix the next free migration number in `PROJECT.md` §Shared files when it is stale (`ls migrations`).

## Step 6: An omp rule (TTSR reminder)

omp loads project rules from `<cwd>/.omp/rules/*.md` when the cwd's `.omp/` directory is not empty ✅ (omp docs, `rulebook-matching-pipeline.md` §native provider). Panels start omp in the main checkout, so the rule reaches them. Create `.omp/rules/evidgate-code-rules.md` with `interruptMode: never`, a `scope` of `tool:edit`/`tool:write` on `*.cs`, `*.ts`, `*.sql`, `*.sh`, `*.yml`, and a `condition` regex for the scan's patterns (scaffold registrations, `as unknown as`, `Skip =`, `.skip(`, `--ignore-exit-code`, `create policy`, task ids in comments). Its body names the rule ids and points to `docs/engineering/agent-code-rules.md` and `REVIEW.md`; do not copy the rules into it, so there is one source of truth.

## Step 7: Report to the owner

Write a short report:
- the files changed, with a diff each;
- the files you expected but did not find;
- blocks skipped because the marker was already present.

Do not commit anything under `.omp/`: it stays local.

## Maintenance

When `agent-code-rules.md`, `REVIEW.md` or the register changes in a way that changes the blocks above, edit this file and bump the marker to `v2`. Re-running the setup then replaces each `v1` block with the `v2` one, rather than skipping it.

After each wave, move the prompts of released sessions (SESSION-CLAIMS `Released`) from `.omp/coordinator/prompts/` and `held/` into `prompts/archive/` (`archive/held/` for held ones). Step 2 skips `archive/`, so a re-run touches only live prompts.

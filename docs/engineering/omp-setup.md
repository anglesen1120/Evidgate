# omp setup: wire the code rules into the agent harness

**Audience:** the omp coordinator (or the owner) right after pulling `develop`.
**Trigger:** the owner says "read `docs/engineering/omp-setup.md` and configure".

The binding rules for coding agents live in [`agent-code-rules.md`](agent-code-rules.md), the review rules in [`../../REVIEW.md`](../../REVIEW.md), and the prioritised fix register in [`../../plans/features/REVIEW-2026-10-29.md`](../../plans/features/REVIEW-2026-10-29.md). omp discovers the root `AGENTS.md` by itself ✅ ([oh-my-pi README](https://github.com/can1357/oh-my-pi): "gitignore + AGENTS.md discovery in one pass"). That file carries only a summary. The task and review prompts under `.omp/` are not in git, so this file says what to add to them.

Every step is idempotent. Before inserting a block, search the target file for the marker line `<!-- evidgate-code-rules v1 -->`. If the marker is there, skip the insertion. Change only the files named below, and report a diff of each change to the owner at the end.

## Step 1: Find the prompt files

1. Run `ls -R .omp/` from the repo root of the main checkout. The `.omp/` folder is untracked and exists only there; worktrees have none.
2. Identify these files and list them in your report:
   - **coder templates:** prompt templates used to hand a task to a coding agent (for example under `.omp/coordinator/prompts/`, including `held/`);
   - **reviewer templates:** prompt templates for review agents (the ones that score `acceptance`, `reviews_cover_final`, `clean_of_blockers`, `handed_over_not_dropped`);
   - **gate spec:** the gate/merge rules, `.omp/coordinator/PROJECT.md` §Gate;
   - **agent definitions:** files under `.omp/agents/`, if present.
3. If a category has no file, say so in the report. Do not invent a new file layout.

## Step 2: Coder templates

Append this block to every coder template, after the task description and before any output-format section:

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

If the coordinator keeps a refill or queue file (for example `.omp/coordinator/prompts/held/README.md` or `next-wave-queue.md`), put the P0 rows CR-01…CR-06 of `plans/features/REVIEW-2026-10-29.md` at the head of the backend lane. That matches the owner priority recorded in `STATUS.md` §1 on 2026-10-29. Use the register's sizes. The next free migration `0055` is reserved for CR-03.

## Step 6: Optional: an omp skill or rule

omp supports skills and rules. Per its README, rules "sit dormant until the model goes off-script" and are injected when a regex matches. The exact file format and location are not verified here ⚠️. Only if the owner asks: create one skill or rule named `evidgate-code-rules` whose body points to `docs/engineering/agent-code-rules.md`. Do not copy the rules into it, so there is one source of truth. Steps 2–4 are the required part; this step is not.

## Step 7: Report to the owner

Write a short report:
- the files changed, with a diff each;
- the files you expected but did not find;
- blocks skipped because the marker was already present.

Do not commit anything under `.omp/`: it stays local.

## Maintenance

When `agent-code-rules.md`, `REVIEW.md` or the register changes in a way that changes the blocks above, edit this file and bump the marker to `v2`. Re-running the setup then replaces each `v1` block with the `v2` one, rather than skipping it.

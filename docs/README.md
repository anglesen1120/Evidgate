# Documentation conventions

Where each kind of documentation lives (plan 01.9.1-T3). A path not listed here needs a reason and a row in the owning feature plan.

| Kind | Location | Owner / rule |
|---|---|---|
| Architecture decision records | [`adr/`](adr/) — [`adr/README.md`](adr/README.md) has the rule and the index | An irreversible choice, a licence exception, or a public contract change ⇒ ADR before merge; `scripts/check-adr.sh` enforces it |
| Runbooks | [`runbooks/`](runbooks/) (created with the first runbook: [`runbooks/environments.md`](runbooks/environments.md), the dev/staging/prod inventory and configuration procedure) | Owned by plan 10 (operations); per-procedure files, e.g. `10.7.1-T1` alert responses |
| Module READMEs | `src/Modules/Evidgate.Modules.<Name>/README.md` | One per module, next to the module project — purpose, owned tables, exported `.Contracts` surface |
| Wire contracts | [`../contracts/README.md`](../contracts/README.md) | Contract pipeline and versioning policy; the policy itself is 00 §9 in [`../plans/00-overview.md`](../plans/00-overview.md) (`/v1` prefixing, breaking-change rules, envelope/schema versioning) |
| Migration playbook | [`migrations-expand-contract.md`](migrations-expand-contract.md) | How a schema change reaches an environment without breaking the running version; owned by plan 01.4.1-T4.1 (runner reference: [`../migrations/README.md`](../migrations/README.md)) |
| Feature plans | [`../plans/features/`](../plans/features/) `<NN>-<area>/NN.E.S-<slug>.md` | One detailed plan per story (00 §12); area plans stay in [`../plans/`](../plans/) |
| Decision memos and their criteria | [`decisions/`](decisions/) — one file per decision, e.g. [`10.8.1-provider-selection-criteria.md`](decisions/10.8.1-provider-selection-criteria.md) | Owner-facing documents that feed an open owner decision (DECISIONS §8): the criteria document and, later, the decision memo for the same story id. Owned by the story that writes them (10.8.1 for O3); they state findings and no vendor until the owner decides |
| Dev setup quickstart | [`dev-setup.md`](dev-setup.md) | Prerequisites, `scripts/dev/up.sh`, ports and `.env.dev` mapping, troubleshooting; owned by 01.8.1 |
| CI quickstart examples | [`quickstart/`](quickstart/) — `github-actions.yml` and `generic.sh` | Rendered by `evidgate quickstart` and kept equal to the renderer by a test; owned by 04.2.4-T2 |
| Source design | [`source/`](source/) | Original Vietnamese design the plan set supersedes where DECISIONS.md §9 lists a delta |
| Repo guide | [`../AGENTS.md`](../AGENTS.md) | Authority order, editing rules, legend |

## Link checking (`scripts/check-links.sh`)

The "lychee or equivalent" required by AC3 is implemented in-repo as `scripts/check-links.sh` (bash + python3, no new dependency — so it carries **no licence row** in ADR-0002, per the same reasoning as the other `scripts/check-*.sh` tools). It checks every `*.md` file that git tracks or that is untracked-but-not-ignored:

- relative links and image targets must resolve to an existing file or directory;
- a `#anchor` on an `.md` target must match a GitHub-style heading slug;
- `http(s)://`, `mailto:` and other external links are skipped;
- links inside fenced code blocks and inline `code` spans are ignored.

It prints `E <file>:<line>: dead link <target>` per failure and exits 1.

## CI job spec (for 01.7.1-T1)

The docs-gate job in the CI pipeline must run, after checkout:

```sh
scripts/check-adr.sh && scripts/check-links.sh
```

Both are executable bash scripts that take no arguments and exit non-zero with `E …` diagnostics on failure. `check-adr-matrix.sh` (ADR-0002 package matrix) runs in the licence-gate job of 01.7.1-T2, not here.

`scripts/check-dor-security.sh` (11.1.1 AC4) runs in the same job as the `check-dor-security` verify stage. It greps the four review questions of the Definition of Ready (authorization, retention/deletion, telemetry, failure path) in `plans/features/README.md` §6 and in `.github/PULL_REQUEST_TEMPLATE.md`, and then in every feature plan added or changed in the change set under review. It takes an optional `--base <ref>` (default: `origin/develop`, then `develop`, then `main`; with no ref resolvable it falls back to the working tree, staged and untracked files), so the CI checkout of that job must be able to resolve the base ref — use `fetch-depth: 0` or pass `--base "${{ github.event.pull_request.base.sha }}"`.

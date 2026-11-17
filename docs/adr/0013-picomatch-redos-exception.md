# ADR-0013 — Time-boxed exception for the picomatch ReDoS advisory (GHSA-fc5q-39xc-vjpf)

Legend: ✅ verified with link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]`.

## Status

accepted (2026-11-18, by the coordinator on the owner's delegation; the owner may override)

## Date

2026-11-18

## Context

`scripts/audit-deps.sh` (stage `audit-deps`, AC6 of 01.7.1-T4.2) runs `npm audit --omit=dev` and fails any finding that is not allowlisted in `scripts/exceptions.json` by advisory id with an ADR. Since 2026-11-17, develop fails that stage with:

```text
fail npm:picomatch@* high GHSA-fc5q-39xc-vjpf — not allowlisted
```

**The advisory.** ✅ [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) / CVE-2026-112366, CWE-1333 (inefficient regular expression complexity): picomatch through 4.0.7 analyses repeated single-branch extglobs incorrectly, so a pattern such as `+(*)` or `+(*a)` compiles to a regex that backtracks exponentially against a short crafted string and blocks the Node.js event loop. GitHub rates it High at CVSS v4 8.7 (`AV:N/AC:L/AT:N/PR:N/UI:N/VC:N/VI:N/VA:H`); npm audit reports CVSS v3.1 7.5 (`AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H`) — the same issue scored on two scales. nvd.nist.gov lists CVE-2026-112366 ✅.

**There is no patch.** ✅ The advisory's *Affected versions* is `<= 4.0.7` and *Patched versions* is **None**; npm still publishes 4.0.7 as `latest` and 2.3.2 as the newest 2.x (`npm view picomatch dist-tags versions`, 2026-11-18). The advisory points at [micromatch/picomatch#194](https://github.com/micromatch/picomatch/pull/194), which is still open, and its own description states that `+(*a)` remains exponential on that branch. No `overrides` entry, fork or git dependency can move the tree out of the affected range, so the choice is an allowlist entry or a red gate.

**Exposure.**

- The only `--omit=dev` chain is **web-only**: `@evidgate/web` → `@angular/localize@22.2.3` → `tinyglobby@0.2.17` → `picomatch@4.0.7`, plus `fdir@6.5.0`'s optional `picomatch` peer (`apps/web/package.json:22-29`, `package-lock.json:1529-1550,16820-16863`). `npm ls picomatch --omit=dev --package-lock-only -w <workspace>` is empty for `packages/cli`, `packages/sdk`, `packages/adapter-junit`, `packages/adapter-playwright` and `packages/test-fixtures`, so the customer-side CLI, Playwright reporter, JUnit adapter and SDK do not ship it. Root tooling (spectral, lint-staged, vitest, @angular/build, @angular/cli) is dev-only and stays in the PR review path, not this gate.
- **Build/localization tooling, not a runtime entry point.** ✅ In the published `@angular/localize@22.2.3` tarball the runtime files `fesm2022/init.mjs:7-11`, `fesm2022/localize.mjs` and `fesm2022/_localize-chunk.mjs` import no glob library; only the CLI bundles do (`tools/bundles/src/extract/cli.js:26,137`, `translate/cli.js:22,340`, `migrate/cli.js:14,71` import and call `tinyglobby.globSync`). The browser build loads `@angular/localize/init` as a polyfill (`apps/web/angular.json:40`) and localizes from the in-repo `src/locale/messages.vi.xlf` (`apps/web/angular.json:11-18,76`).
- **Who must control what.** Upstream, the extractor takes the glob from its `--source` flag (`tools/bundles/src/extract/cli.js:85-89`), tinyglobby compiles it with picomatch (`dist/index.mjs:217-219`) and matches filesystem paths discovered by the fdir crawler (`:228-236`). An attacker therefore needs to control the **glob pattern**; controlling only the matched path is not enough while the pattern is fixed. For our tree the pattern comes from a developer/CI command line or repository build configuration, and no first-party call passes request, upload or repository content into a glob: a search of `apps/web`, `packages` and `scripts` finds no import or call of picomatch, micromatch, tinyglobby or fdir, and only test imports of `clearTranslations`/`loadTranslations` from `@angular/localize` ⚠️ (searched 2026-11-18; the emitted bundle was not inspected, see Evidence).

## Decision

1. `[DECISION]` **Allowlist the advisory for 30 days.** Add to `scripts/exceptions.json` one entry keyed `GHSA-fc5q-39xc-vjpf`, naming this ADR, the package `picomatch`, the reason in decision 2 and `"expires": "2026-12-18"`. No `overrides` entry, no dependency or lockfile change, no second exception in the same commit.
2. `[DECISION]` **Residual risk accepted is build-time only.** The unfixed matcher is reachable from the localization/build CLI, whose glob pattern is supplied by repository configuration or a developer command; the advisory requires an attacker-controlled pattern and there is no production, browser, server or customer-side runtime path in the production closure established above. A malicious pattern committed to this repository is already a change under review, and any reviewer-approved run of the localization CLI is the remaining exposure.
3. `[DECISION]` **The exception ends early and unconditionally** when either happens:
   - a patched picomatch version is published (`npm view picomatch dist-tags` shows anything above 4.0.7, or a 2.3.x/3.x outside the advisory's range), and the lock is updated in the same change that removes this entry;
   - a new `--omit=dev` chain appears that passes an untrusted glob pattern or matched string to picomatch (for example a server route or customer CLI that globs request-controlled values).
4. `[DECISION]` **Re-check procedure, at the expiry and before it:** run `npm view picomatch dist-tags versions --json` for a patched release; run `npm ls picomatch --all --omit=dev --package-lock-only` to re-derive the production chains; run `npm audit --omit=dev` and `scripts/audit-deps.sh --npm-only` to confirm the gate now reports the finding as exempted with zero failing rows. Remove the entry and this ADR's allowance at once when decision 3 fires; do not extend the expiry without a new owner decision recorded here.
5. `[DECISION]` **Stated limit of the analysis.** The browser-bundle conclusion is reasoned from the published package sources and `angular.json`, not from an inspected emitted bundle: no production build exists in the tree, and one was deliberately not run for this change. A later build is the check that confirms it.

## Alternatives rejected

| Alternative | Reason rejected |
|---|---|
| Upgrade picomatch to a patched version | ✅ No patched release exists (`<= 4.0.7` affected, patched: none; latest published is 4.0.7, 2.x is 2.3.2 as of 2026-11-18) |
| `overrides` / fork / git dependency pinning a patched commit | The fix ([micromatch/picomatch#194](https://github.com/micromatch/picomatch/pull/194)) is an unmerged PR that its own description says does not fix `+(*a)`; shipping an unreleased fork of a glob matcher used by the build is a larger supply-chain risk than the time-boxed exception |
| Downgrade or drop `@angular/localize` | npm's suggested `fixAvailable` is a semver-major downgrade to `@angular/localize@19.2.25`; the app's i18n contract (ADR-0002, ADR-0008) is built on Angular 22 and `$localize`, so this trades a build-time ReDoS for an unsupported framework combination |
| Move `@angular/localize` to `devDependencies` | It is a real production import: the browser build loads `@angular/localize/init` and every `$localize` message in the bundle depends on it; moving it would break `npm ci --omit=dev` installs of the workspace |
| Leave the gate red and block develop | The finding cannot be fixed by a dependency change in this repository until upstream releases a patch; a permanently red gate trains people to ignore `audit-deps` and blocks unrelated work |
| Blanket `npm audit` suppression (config flag, `--audit-level`, ignore file) | Bends the gate (AGENTS.md "never bend a gate"): it would hide every future advisory, not one reviewed id with an expiry |

## Consequences

- The `audit-deps` stage on develop goes green again while the finding is reported as `warn ... — exempted by ADR-0013`; the exemption is visible in every gate log and expires automatically on the day after 2026-12-18.
- One documented, owner-delegated acceptance of a High-severity build-time ReDoS remains until upstream ships a patched picomatch. The 30-day window matches the advisory's recency (reviewed 2026-11-17) and the likely cadence of a picomatch release.
- No application code, schema, contract, lockfile or manifest changes; no migration. The only files are this ADR, the index row and the exceptions entry.
- Follow-up work: the re-check in decision 4 belongs to whoever next touches dependency gates, and the expiry date is itself the deadline; if upstream still has not released a patch by then, the owner must decide again (a new ADR or a documented renewal), not silently extend this one.

## Evidence

- ✅ [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) — CWE-1333, affected `<= 4.0.7`, patched none, CVSS v4 8.7, references [CVE-2026-112366](https://nvd.nist.gov/vuln/detail/CVE-2026-112366), [micromatch/picomatch#194](https://github.com/micromatch/picomatch/pull/194) and the vulncheck advisory; read 2026-11-18, advisory last updated 2026-11-17.
- ✅ `npm view picomatch dist-tags versions --json` (2026-11-18): `latest: 4.0.7`, versions end at 4.0.7; 2.x ends at 2.3.2.
- ✅ `npm ls picomatch --all --omit=dev --package-lock-only --workspaces --include-workspace-root`: only `@evidgate/web` → `@angular/localize@22.2.3` → `tinyglobby@0.2.17` → `picomatch@4.0.7` (`fdir@6.5.0` optional peer deduped). Per-workspace runs for `packages/{cli,sdk,adapter-junit,adapter-playwright,test-fixtures}` printed `(empty)`.
- ✅ `npm audit --omit=dev --json`: exit 1, 4 high entries that are this one advisory propagated to `@angular/localize`, `fdir`, `picomatch` and `tinyglobby`; the only object `via` carrying a GHSA is picomatch's.
- ✅ `scripts/audit-deps.sh --npm-only`: exit 1, `fail npm:picomatch@* high GHSA-fc5q-39xc-vjpf — not allowlisted`, summary `audit-deps: 1 findings — 0 exempted by exceptions.json, 1 failing` (before this entry). After adding the entry: exit 0, `warn npm:picomatch@* high GHSA-fc5q-39xc-vjpf — exempted by ADR-0013`, `audit-deps: 1 findings — 1 exempted by exceptions.json, 0 failing`.
- ✅ Published `@angular/localize@22.2.3` tarball ([registry](https://registry.npmjs.org/@angular/localize/-/localize-22.2.3.tgz)): `package.json` exports `./init` → `fesm2022/init.mjs` and `.` → `fesm2022/localize.mjs`; those files and `_localize-chunk.mjs` import no glob library; `tinyglobby` is imported only by `tools/bundles/src/{extract,translate,migrate}/cli.js` (lines 26/137, 22/340, 14/71). `tinyglobby@0.2.17` ([registry](https://registry.npmjs.org/tinyglobby/-/tinyglobby-0.2.17.tgz)) compiles patterns at `dist/index.mjs:217-219` and matches crawler paths at `:228-236`; `fdir@6.5.0` ([registry](https://registry.npmjs.org/fdir/-/fdir-6.5.0.tgz)) loads picomatch only for its own optional `.glob` API (`dist/index.mjs:442-445,551-564`), which the tinyglobby crawler path does not call.
- ✅ Repository search for `from`/`require` of `picomatch`, `micromatch`, `tinyglobby` or `fdir` across tracked files: no hits; `@angular/localize/tools` is never imported; `@angular/localize` appears only as test imports of `clearTranslations`/`loadTranslations` plus the build polyfill and ambient types.
- ⚠️ The emitted browser bundle was **not inspected**: no `apps/web/dist` or root `dist` exists in the worktree and no build was run for this change, so "no glob library in the bundle" is reasoned from package exports and `angular.json`, not observed output.

## Revisit trigger

Reopen this ADR before or at 2026-12-18, or immediately when a patched picomatch release appears, when a new production dependency chain passes untrusted glob patterns or matched strings to picomatch, when `@angular/localize` stops being a browser polyfill dependency, or when an inspected production bundle is found to include the vulnerable matcher. Renewal requires a fresh owner decision with a new expiry; this ADR is not silently extendable.

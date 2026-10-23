# Contributing

Evidgate's contract is the plan set under `plans/` + `DECISIONS.md`. Read
`plans/00-overview.md` and your story's feature plan
(`plans/features/NN-area/NN.E.S-*.md`) before writing code — they are binding
(e.g. the permission model, module shape, CQRS rules).

## Branches

- Base branch is `develop`; `main` is the stable planning baseline.
- Feature branches are named `feature/<NN.E.S>-<slug>` and branch off
  `develop`, e.g. `feature/01.2.1-quality-gates` for story `01.2.1`.
- Never force-push a shared branch.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/): `type(scope):
subject`, e.g. `feat(ingestion): accept JUnit XML uploads`, `fix(api): reject
unknown enum values`. Common types: `feat`, `fix`, `refactor`, `test`, `docs`,
`chore`, `build`, `ci`.

## Before opening a PR

```bash
scripts/verify.sh
```

`verify.sh` is the local gate that CI (01.7.1) runs verbatim: restore, build
with warnings-as-errors, `dotnet format` and Prettier checks, unit +
architecture tests, `tsc -b`, ESLint and the contract/layout/ADR check scripts.
A PR with a failing stage fails CI. Required status checks (so a red PR
cannot be merged at all) are 01.7.1-T3.1: they need the repository to be public
or on a paid plan — on this private repo the GitHub API answers 403 'Upgrade to
GitHub Pro or make this repository public', so until the owner decides, the
reviewer convention is what blocks a red PR (see STATUS.md §6).

Every dependency you add (NuGet `PackageVersion`/`GlobalPackageReference` or
npm `dependencies`/`devDependencies`) must be registered in
`docs/adr/0002-compatibility-and-licence-matrix.md` first —
`scripts/check-adr-matrix.sh` enforces it — and its licence must satisfy
DECISIONS §4 (MIT/Apache-2.0/BSD/ISC; no AGPL/SSPL/RPL/BUSL). The same licence
list is machine-enforced by `scripts/licence-scan.sh`, which gates CI on both
ecosystems: a deny-listed licence fails unless the package is listed in
`scripts/licence-policy.json` `exceptions` keyed by the accepting ADR id
(LGPL/MPL-level licences are warn-level — the ADR row is required, not
optional). `scripts/audit-deps.sh` applies the same ADR-keyed rule to
vulnerability advisories via `scripts/exceptions.json`.

Dependency changes need reviewer scrutiny too: flag any new package that runs
install scripts (npm `preinstall`/`postinstall`) or ships NuGet build targets
in the PR checklist, and Dependabot PRs are held to the same gates — a bump
still needs its ADR row and must pass the licence and audit checks. The PR
template asks for the exact command and observed output as test evidence for
each acceptance criterion; reviewers enforce it by convention.

The pre-commit hook (husky + lint-staged) runs the same linters on staged files
in check mode only; it never modifies your files.

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
A PR with a failing stage will be blocked by required checks.

Every dependency you add (NuGet `PackageVersion`/`GlobalPackageReference` or
npm `dependencies`/`devDependencies`) must be registered in
`docs/adr/0002-compatibility-and-licence-matrix.md` first —
`scripts/check-adr-matrix.sh` enforces it — and its licence must satisfy
DECISIONS §4 (MIT/Apache-2.0/BSD/ISC; no AGPL/SSPL/RPL/BUSL).

The pre-commit hook (husky + lint-staged) runs the same linters on staged files
in check mode only; it never modifies your files.

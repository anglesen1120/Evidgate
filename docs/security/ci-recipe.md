# Pinning and least privilege for customer CI

Owner: 11.6.1-T2.1 (task [11.6.1-T2](../../plans/features/11-security-compliance/11.6.1-every-dependency-is-a-decision-acceptance.md), AC3/AC4). The full fork-safe recipe is [customer-ci-recipe.md](customer-ci-recipe.md); this page adds the two rules a customer's own repository must keep, and how to check them.

Legend: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]`. GitHub citations are pages on docs.github.com read on 2026-10-10.

## 1. The two rules

1. **SHA-pin every action and container image.** A tag or branch is mutable, so `uses: owner/action@v5` can change under you; pin the full 40-character commit SHA and keep the release tag in a `# vX.Y.Z` comment. A `docker://` image needs `@sha256:<64 hex>`. This is rule `R2-sha-pinned` in [customer-ci-recipe.md §3](customer-ci-recipe.md#3-the-five-rules), with its GitHub source.
2. **Give `GITHUB_TOKEN` the least it needs.** Set the workflow default to `permissions: {}` (or read-only), elevate per job, and grant `id-token: write` only on the job that mints the Evidgate token (the `upload` job in the recipe and the quickstart template). This is rule `R3-token-permissions` in [customer-ci-recipe.md §3](customer-ci-recipe.md#3-the-five-rules).

The fork-safe recipe already follows both, and the `evidgate quickstart` template renders the same shape.

## 2. Keeping the pins current

A SHA pin does not update itself. In the customer's own repository, add a `github-actions` entry to `.github/dependabot.yml`; Dependabot then opens a PR when a pinned action has a newer release, and the reviewer re-pins to the new SHA (keeping the `# vX.Y.Z` comment). ✅ [Dependabot options reference](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference) lists `github-actions` as a `package-ecosystem`.

Pin the Evidgate CLI to an exact version too: the rendered steps use `npx --yes --package @evidgate/cli@<version>` with the exact published version, never a range or `latest`. An integrity-pinned install (a committed lockfile with `npm ci --ignore-scripts`, or a checksummed binary) is not available yet ⚠️ (04.9.2-T1).

## 3. Checking a workflow

`scripts/lint-ci-recipe.sh <file>` checks the five rules on any workflow file; it needs Node.js and the Evidgate repository's `npm ci` (for the `yaml` parser). Run it from an Evidgate checkout:

```sh
scripts/lint-ci-recipe.sh /path/to/your-repo/.github/workflows/evidgate.yml
```

It exits 0 when the file is clean, 1 on a violation, 2 on a usage or I/O error ([customer-ci-recipe.md §9](customer-ci-recipe.md#9-checking-a-workflow-scriptslint-ci-recipesh)). The `evidgate quickstart` output passes it (11.6.1-T2): `docs/quickstart/github-actions.yml` is the rendered template and lints clean.

## 4. How Evidgate itself follows these rules

The same two rules are enforced on Evidgate's own workflows by the shipped 01.7.1-T4 artefacts:

- **SHA pins (AC3).** [`scripts/check-action-pins.sh`](../../scripts/check-action-pins.sh) fails a workflow whose `uses:` is a tag or branch, or is SHA-pinned without the `# vX.Y.Z` comment (`:60-75`); it also rejects quoted or flow-style `uses` keys that would bypass the line-anchored scan (`:23-39`).
- **Keeping pins current (AC4).** [`.github/dependabot.yml`](../../.github/dependabot.yml) configures the `github-actions` ecosystem (`:53-73`) alongside `nuget` (`:9-28`) and `npm` (`:30-51`), so a pinned action gets an update PR; `check-action-pins.sh` still requires the version comment on the new SHA.

## 5. Not covered

- The lint is not shipped to customers as a CLI command or a published package; it stays an Evidgate-repository script ([customer-ci-recipe.md §9](customer-ci-recipe.md#9-checking-a-workflow-scriptslint-ci-recipesh)).
- The rules cover a GitHub Actions workflow only; other CIs use the ingest-token grant ([customer-ci-recipe.md §5](customer-ci-recipe.md#5-non-oidc-cis-the-ingest-token-grant-explicit-provide)).

# Pinning and least privilege for customer CI

Owner: 11.6.1-T2.1 (task [11.6.1-T2](../../plans/features/11-security-compliance/11.6.1-every-dependency-is-a-decision-acceptance.md), AC3/AC4). The full fork-safe recipe is [customer-ci-recipe.md](customer-ci-recipe.md); this page adds the two rules a customer's own repository must keep, and how to check them.

Legend: ✅ verified with a link · ⚠️ unverified · `[INFERENCE]`. GitHub citations are pages on docs.github.com read on 2026-10-10.

## 1. The two rules

1. **Pin pipeline dependencies.** Use immutable commit or image digests and review updates; do not execute an unpinned installer in the upload job.
2. **Isolate the credential.** Keep the masked project ingest token in a trusted upload job that does not execute untrusted code. A fork job receives no Evidgate credential. See the [customer recipe](customer-ci-recipe.md#3-the-five-rules).

## 2. Keeping the pins current

Use the update tooling of your own CI provider to propose, review and refresh pinned dependencies.

Pin the Evidgate CLI to an exact version too: the rendered steps use `npx --yes --package @evidgate/cli@<version>` with the exact published version, never a range or `latest`. An integrity-pinned install (a committed lockfile with `npm ci --ignore-scripts`, or a checksummed binary) is not available yet ⚠️ (04.9.2-T1).

## 3. Checking a workflow

For the repository’s historical Actions-format fixture only, `scripts/lint-ci-recipe.sh <file>` checks workflow text using Node.js and the `yaml` parser. It is not a security proof for another CI system:

```sh
scripts/lint-ci-recipe.sh /path/to/your-repo/.github/workflows/evidgate.yml
```

The lint exits 0 on clean input, 1 on a violation and 2 on usage or I/O errors. No lint result proves the project secret is unreachable from untrusted jobs; verify the runner settings.

## 4. How Evidgate itself follows these rules

The same two rules are enforced on Evidgate's own workflows by the shipped 01.7.1-T4 artefacts:

- **SHA pins (AC3).** [`scripts/check-action-pins.sh`](../../scripts/check-action-pins.sh) fails a workflow whose `uses:` is a tag or branch, or is SHA-pinned without a trailing comment (`:60-75`); it also rejects quoted or flow-style `uses` keys that would bypass the line-anchored scan (`:23-39`). The checker only requires a non-empty comment; keeping the release tag in a `# vX.Y.Z` comment is the convention this page recommends (§1, §2).
- **Keeping pins current (AC4).** [`.github/dependabot.yml`](../../.github/dependabot.yml) configures the `github-actions` ecosystem (`:53-73`) alongside `nuget` (`:9-28`) and `npm` (`:30-51`), so a pinned action gets an update PR; `check-action-pins.sh` still requires a comment on the new SHA (the `# vX.Y.Z` convention).

## 5. Not covered

- The lint is not shipped to customers as a CLI command or a published package; it stays an Evidgate-repository script ([customer-ci-recipe.md §9](customer-ci-recipe.md#9-checking-a-workflow-scriptslint-ci-recipesh)).
- Customer CI may use any provider. The authenticated Evidgate path is the [project ingest-token grant](customer-ci-recipe.md#5-project-ingest-token-grant).

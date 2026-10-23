// 04.2.4-T2.1: `evidgate quickstart` — renders a CI recipe for this project to stdout
// or to a file (04.2.4 §2 AC5, §5, §9). It is a pure local template: no network call,
// no token read, no cache read (`requireToken: false`). The GitHub template uses the
// OIDC exchange (`id-token: write`) so no long-lived secret is ever stored; the only
// stored setting is the repository configuration VARIABLE `EVIDGATE_API_URL`.
//
// The rendered CLI pin is the exact `version` from `packages/cli/package.json`
// (`readCliVersion`), never a range or `latest`. The project value is interpolated
// into YAML and shell, so it is guarded by `renderableProject` first: a value that
// could break out of the template (a `${{ }}` expression, a quote, a colon, a leading
// dash) is refused as `invalid_config` and never echoed.
import { lstatSync } from 'node:fs';
import { mkdir, open, realpath, unlink } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';

import { CliError, escapeControlCharacters } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { readCliVersion } from '../version.js';

import type { FileHandle } from 'node:fs/promises';
import type { CommandDefinition } from './index.js';

/** The two templates `--format` selects; `github` is the default. */
export type QuickstartFormat = 'github' | 'generic';

export interface QuickstartRender {
  readonly format: QuickstartFormat;
  readonly project: string;
  readonly version: string;
}

/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level usage errors carry the
 * same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";

/**
 * A project value safe to interpolate into a YAML scalar and a shell single-quoted
 * string: an alphanumeric first character, then alphanumerics, `.`, `_` and `-`, up to
 * 128 characters. This refuses `${{ … }}`, quotes, colons, whitespace and a leading
 * `-`, so the rendered template cannot be broken out of (04.2.4 §8).
 */
const renderableProject = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

/** The pinned action SHAs, kept identical to `.github/workflows/ci.yml` (AC5). */
const checkoutAction = 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1';
const setupNodeAction = 'actions/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7.0.0';

export const quickstartCommand: CommandDefinition = {
  name: 'quickstart',
  summary: 'print a CI recipe for this project',
  requireToken: false,
  options: { format: { type: 'string' }, write: { type: 'string' } },
  // Runs before `main`'s `--verbose` dump, so an unrenderable project is refused
  // without that dump echoing the value (04.2.4 §8).
  validateConfig: (config) => {
    readProject(config.project);
  },
  run: async ({ config, io, values }) => {
    const format = readFormat(values.format);
    const project = readProject(config.project);
    const text = renderQuickstart({ format, project, version: readCliVersion() });
    const write = values.write;
    if (write === undefined) {
      io.stdout(text);
      return ExitCode.Ok;
    }
    if (typeof write !== 'string' || write.length === 0) {
      throw usageError('missing required flag --write');
    }
    const written = await writeTemplate(write, text);
    // S-2: the path is echoed back, so escape control/format characters (§5) — a
    // filename can carry them, and they must not forge a log line.
    io.stdout(`wrote ${escapeControlCharacters(written)}\n`);
    return ExitCode.Ok;
  },
};

/**
 * Renders the recipe for `format`, pinned to `version` and scoped to `project`. Pure:
 * the only input is the argument object, so a spec can compare the output byte-for-byte
 * with the committed `docs/quickstart/` examples.
 */
export function renderQuickstart({ format, project, version }: QuickstartRender): string {
  return format === 'generic' ? renderGeneric(project, version) : renderGithub(project, version);
}

/** `--format` defaults to `github`; any other value is a usage error (AC5). */
function readFormat(value: unknown): QuickstartFormat {
  if (value === undefined) {
    return 'github';
  }
  if (value === 'github' || value === 'generic') {
    return value;
  }
  throw usageError('--format must be github or generic');
}

/** Refuses a project the template cannot render; the value is never echoed (AC5). */
function readProject(project: string): string {
  if (!renderableProject.test(project)) {
    throw new CliError(
      'invalid configuration: EVIDGATE_PROJECT (or --project): not renderable in a CI template',
      { code: 'invalid_config' },
    );
  }
  return project;
}

function usageError(message: string): CliError {
  return new CliError(message, { code: 'usage', hint: helpHint });
}

/** The pinned invocation of the published CLI, shared by both templates. */
function npx(version: string, args: string): string {
  return `npx --yes --package @evidgate/cli@${version} -- evidgate ${args}`;
}

/**
 * The GitHub Actions template. Fork-safe by construction (11.6.1-T2, mirroring
 * `fixtures/ci-recipes/github-fork-safe.yml`): the workflow grants nothing at the top
 * level; the credential-free `test` job runs on `pull_request` and `push`; the `upload`
 * job that mints the token runs only on `workflow_dispatch` or a push to the default
 * branch, inside the secretless environment `evidgate-upload`, with `contents: read` +
 * `id-token: write` so the exchange step can present the GitHub OIDC JWT.
 * `EVIDGATE_API_URL` is a repository configuration VARIABLE, the only stored setting.
 * No `secrets.` reference exists. The rules and their sources are in
 * `docs/security/customer-ci-recipe.md`.
 */
function renderGithub(project: string, version: string): string {
  return `# Evidgate quickstart — GitHub Actions workflow (rendered by \`evidgate quickstart\`).
#
# The only stored setting is the repository configuration VARIABLE EVIDGATE_API_URL
# (Settings → Secrets and variables → Actions → Variables). No secret is stored: the
# CLI exchanges the GitHub OIDC token for a short-lived Evidgate CI token.
#
# Fork-safe by construction: \`test\` runs on pull_request and holds no Evidgate setting
# and no token; \`upload\` mints the token only on a workflow_dispatch or push from the
# default branch. The rules and their sources: docs/security/customer-ci-recipe.md.
name: evidgate

on:
  push:
    branches:
      - main
  pull_request:
  workflow_dispatch:

# R3: nothing at workflow level; a job without its own permissions block gets nothing.
permissions: {}

jobs:
  test:
    if: "github.event_name != 'workflow_dispatch'"
    runs-on: ubuntu-24.04
    # R3: read the code, nothing else. A fork pull request gets a read-only token anyway.
    permissions:
      contents: read
    steps:
      - uses: ${checkoutAction}
        with:
          persist-credentials: false
      - uses: ${setupNodeAction}
        with:
          node-version: "22"
      # Replace with your build and test commands. This job holds no Evidgate setting
      # and no token, so a fork pull request runs it safely.
      - run: npm ci
      - run: npm test

  upload:
    needs:
      - test
    # Runs only on a maintainer's workflow_dispatch from the default branch (the test
    # job is skipped then) or after a green push to the default branch. Never for a
    # pull_request event.
    if: "\${{ !cancelled() && ((github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main') || (github.event_name == 'push' && github.ref == 'refs/heads/main' && needs.test.result == 'success')) }}"
    runs-on: ubuntu-24.04
    # An environment with no secrets: create it with required reviewers and a
    # deployment branch rule for \`main\` before first use — its rules gate the job and
    # it holds nothing a compromised step could read. Referencing an environment that
    # does not exist auto-creates it with no protection rules:
    # https://docs.github.com/en/actions/deployment/targeting-different-environments/using-environments-for-deployment
    environment: evidgate-upload
    # R3: per-job elevation. id-token write lets the CLI request the GitHub OIDC token.
    permissions:
      contents: read
      id-token: write
    env:
      EVIDGATE_API_URL: \${{ vars.EVIDGATE_API_URL }}
      EVIDGATE_PROJECT: "${project}"
    steps:
      - uses: ${checkoutAction}
        with:
          persist-credentials: false
      - uses: ${setupNodeAction}
        with:
          node-version: "22"
      # Exchange the GitHub OIDC token for a short-lived Evidgate CI token. The token
      # is written only to the 0600 cache, never printed.
      - run: ${npx(version, 'auth login')}
      # Upload the manifest and keep its ids for the run step. Each id is registered
      # with ::add-mask:: before the $GITHUB_OUTPUT write: R4 in
      # docs/security/customer-ci-recipe.md requires a masked payload there (the
      # runner then redacts the ids in the log).
      - id: manifest
        run: |
          out=$(${npx(version, 'manifests upload --file evidgate-manifest.json --json')})
          manifest_id=$(printf '%s' "$out" | jq -r .manifest_id)
          manifest_hash=$(printf '%s' "$out" | jq -r .manifest_hash)
          echo "::add-mask::$manifest_id"
          echo "::add-mask::$manifest_hash"
          echo "manifest_id=$manifest_id" >> "$GITHUB_OUTPUT"
          echo "manifest_hash=$manifest_hash" >> "$GITHUB_OUTPUT"
      # Create the run for that manifest. The ids travel in the step env, never
      # interpolated into the script. GITHUB_REF is a full ref for this job's events
      # (refs/heads/main); the CLI also accepts refs/tags/<tag> and
      # refs/pull/<n>/merge.
      - env:
          MANIFEST_ID: \${{ steps.manifest.outputs.manifest_id }}
          MANIFEST_HASH: \${{ steps.manifest.outputs.manifest_hash }}
        run: ${npx(version, 'runs create --manifest-id "$MANIFEST_ID" --manifest-hash "$MANIFEST_HASH" --ci-ref "$GITHUB_REF"')}
      # Not yet available — these steps land with their owning tasks:
      #   events send + runs finalize (04.2.3-T1/T2)
      #   artifacts upload (04.2.4-T1)
`;
}

/**
 * The generic template: a POSIX `sh` script, not YAML ([DECISION] V1 CI integration is
 * GitHub Actions; other CIs are per-project configuration, 00 §1 decision 2026-10-02).
 * `EVIDGATE_API_URL` comes from the CI's non-secret variables; the credential is named
 * only — `EVIDGATE_OIDC_TOKEN` or `EVIDGATE_INGEST_TOKEN` — and injected by the CI.
 */
function renderGeneric(project: string, version: string): string {
  return `#!/bin/sh
# Evidgate quickstart — generic CI recipe (rendered by \`evidgate quickstart\`).
#
# Set EVIDGATE_API_URL in your CI's non-secret variables. Nothing else is stored:
# the CLI authenticates with a short-lived credential the CI injects.
#
# Credential, by name only — never put a value in this file:
#   EVIDGATE_OIDC_TOKEN    a short-lived OIDC JWT the project binding accepts, or
#   EVIDGATE_INGEST_TOKEN  a project-scoped, revocable, rotatable ingest token
#                          created with an expiry (02.4.2), injected by the CI's
#                          masked secret store. Never committed, never echoed.
set -eu

: "\${EVIDGATE_API_URL:?EVIDGATE_API_URL must be set in the CI variables}"
export EVIDGATE_API_URL
export EVIDGATE_PROJECT='${project}'

# Exchange the credential for a short-lived Evidgate CI token. The token is written
# only to the 0600 cache, never printed.
${npx(version, 'auth login')}

# Upload the manifest and keep its ids in shell variables for the run step.
out=$(${npx(version, 'manifests upload --file evidgate-manifest.json --json')})
manifest_id=$(node -p 'JSON.parse(process.argv[1]).manifest_id' "$out")
manifest_hash=$(node -p 'JSON.parse(process.argv[1]).manifest_hash' "$out")

# Create the run for that manifest. Add --ci-ref refs/heads/<branch> (or
# refs/tags/<tag>) here to record where the run came from.
${npx(version, 'runs create --manifest-id "$manifest_id" --manifest-hash "$manifest_hash"')}

# Not yet available — these steps land with their owning tasks:
#   events send + runs finalize (04.2.3-T1/T2)
#   artifacts upload (04.2.4-T1)
`;
}

/**
 * Injectable fs seam for `writeTemplate`: a spec replaces `writeFile` to exercise the
 * post-`open` cleanup path (CR-2) without a real disk-full or I/O failure.
 */
export interface WriteTemplateDeps {
  /** Replaces `handle.writeFile`; the default writes `text` as UTF-8. */
  readonly writeFile?: (handle: FileHandle, text: string) => Promise<void>;
}

/**
 * Writes `text` to `target`, which must resolve inside the current working directory.
 * The escape check resolves symlinks on both sides — `realpath(cwd)` and the realpath of
 * the nearest existing parent of the target — so a `..` escape and a symlinked parent
 * that points outside are both refused. Comparing the raw `resolve` output against `cwd`
 * would wrongly reject a path that reaches the cwd through a symlink alias (macOS
 * `/tmp` → `/private/tmp`), so only the realpath comparison decides. The file is created
 * with the exclusive `wx` flag (never overwriting) and mode 0o644, and missing parent
 * directories inside the cwd are created. Returns the path relative to the cwd.
 *
 * Every filesystem failure is mapped to a `CliError` (CR-1): a path-shape errno is
 * `usage`, anything else is `write_failed`, and the message is fixed — it never carries
 * the Node error text or an absolute path.
 */
export async function writeTemplate(
  target: string,
  text: string,
  deps: WriteTemplateDeps = {},
): Promise<string> {
  const cwd = process.cwd();
  const resolved = resolve(cwd, target);
  const existing = nearestExisting(dirname(resolved));
  let handle: FileHandle | undefined;
  let realTarget = '';
  try {
    const realCwd = await realpath(cwd);
    const realExisting = await realpath(existing);
    if (!isInside(realCwd, realExisting)) {
      throw usageError('--write must be inside the current working directory');
    }
    // The real path of the target: the existing ancestor resolved through symlinks plus
    // the not-yet-created tail, so `wrote <path>` is correct even through an alias.
    realTarget = join(realExisting, relative(existing, resolved));
    await mkdir(dirname(realTarget), { recursive: true });
    handle = await open(realTarget, 'wx', 0o644);
    const write =
      deps.writeFile ?? ((file: FileHandle, body: string) => file.writeFile(body, 'utf8'));
    await write(handle, text);
    await handle.close();
    handle = undefined;
    // S-1: re-check containment after creation. A local actor with write access inside
    // the cwd could have swapped a parent for a symlink between the check above and the
    // create; if the created file now resolves outside, remove it and refuse.
    const realCreated = await realpath(realTarget).catch(() => undefined);
    if (realCreated === undefined || !isInside(realCwd, realCreated)) {
      await unlink(realCreated ?? realTarget).catch(() => undefined);
      throw usageError('--write path escapes the working directory');
    }
    return relative(realCwd, realTarget);
  } catch (error) {
    if (error instanceof CliError) {
      throw error;
    }
    if (handle !== undefined) {
      // CR-2: a failed write must not leave the created file behind, or a retry would
      // hit `file exists` on the exclusive create.
      await cleanupFailedWrite(handle, realTarget);
    }
    throw writeError(error);
  }
}

/**
 * True when `candidate` is `base` itself or sits under it, decided with `path.relative`
 * so the check is correct when `base` is the filesystem root (S-1): the old
 * `startsWith(base + sep)` test compared against `//` for `/` and wrongly rejected every
 * path. Exported for the pure unit test.
 */
export function isInside(base: string, candidate: string): boolean {
  const rel = relative(base, candidate);
  return rel === '' || (rel !== '..' && !rel.startsWith(`..${sep}`) && !isAbsolute(rel));
}

/**
 * Maps a filesystem failure in the `--write` path to a `CliError` (CR-1). Path-shape
 * errno codes are `usage`; every other failure — permissions, read-only filesystem,
 * full disk, I/O — is `write_failed`. Each message is fixed and never contains the Node
 * error message or an absolute path.
 */
function writeError(error: unknown): CliError {
  switch ((error as NodeJS.ErrnoException | null)?.code) {
    case 'ENOENT':
      return usageError('cannot write --write path: no such directory');
    case 'ENOTDIR':
      return usageError('cannot write --write path: not a directory');
    case 'EISDIR':
      return usageError('cannot write --write path: is a directory');
    case 'EEXIST':
      return usageError('file exists');
    default:
      return new CliError('cannot write --write path', { code: 'write_failed' });
  }
}

/** CR-2: closes the handle and best-effort removes the file a failed write created. */
async function cleanupFailedWrite(handle: FileHandle, path: string): Promise<void> {
  await handle.close().catch(() => undefined);
  await unlink(path).catch(() => undefined);
}

/**
 * The nearest ancestor of `path` (including `path` itself) that exists, using `lstat`
 * so a symlink counts as existing even when its target does not. `realpath` of this
 * ancestor is what the escape check resolves.
 */
function nearestExisting(path: string): string {
  let current = path;
  for (;;) {
    try {
      lstatSync(current);
      return current;
    } catch {
      const parent = dirname(current);
      if (parent === current) {
        return current;
      }
      current = parent;
    }
  }
}

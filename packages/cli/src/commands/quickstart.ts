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
import { mkdir, open, realpath } from 'node:fs/promises';
import { dirname, join, relative, resolve, sep } from 'node:path';

import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { readCliVersion } from '../version.js';

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
    io.stdout(`wrote ${written}\n`);
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
 * The GitHub Actions template. `permissions` grants `id-token: write` so the exchange
 * step can present the GitHub OIDC JWT; `EVIDGATE_API_URL` is a repository
 * configuration VARIABLE, the only stored setting. No `secrets.` reference exists.
 */
function renderGithub(project: string, version: string): string {
  return `# Evidgate quickstart — GitHub Actions workflow (rendered by \`evidgate quickstart\`).
#
# The only stored setting is the repository configuration VARIABLE EVIDGATE_API_URL
# (Settings → Secrets and variables → Actions → Variables). No secret is stored: the
# CLI exchanges the GitHub OIDC token for a short-lived Evidgate CI token.
#
# Fork pull requests get no OIDC token, so the exchange step below fails there; add
# \`if: github.event_name != 'pull_request'\` to skip it on forks.
name: evidgate

on:
  push:
  pull_request:

permissions:
  contents: read
  id-token: write

jobs:
  evidgate:
    runs-on: ubuntu-24.04
    env:
      EVIDGATE_API_URL: \${{ vars.EVIDGATE_API_URL }}
      EVIDGATE_PROJECT: "${project}"
    steps:
      - uses: ${checkoutAction}
      - uses: ${setupNodeAction}
        with:
          node-version: "22"
      # Exchange the GitHub OIDC token for a short-lived Evidgate CI token. The token
      # is written only to the 0600 cache, never printed.
      - run: ${npx(version, 'auth login')}
      - run: ${npx(version, 'manifests upload --file evidgate-manifest.json')}
      # Not yet available — these steps land with their owning tasks:
      #   runs create (04.2.2-T2)
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
export EVIDGATE_PROJECT='${project}'

# Exchange the credential for a short-lived Evidgate CI token. The token is written
# only to the 0600 cache, never printed.
${npx(version, 'auth login')}

${npx(version, 'manifests upload --file evidgate-manifest.json')}

# Not yet available — these steps land with their owning tasks:
#   runs create (04.2.2-T2)
#   events send + runs finalize (04.2.3-T1/T2)
#   artifacts upload (04.2.4-T1)
`;
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
 */
async function writeTemplate(target: string, text: string): Promise<string> {
  const cwd = process.cwd();
  const resolved = resolve(cwd, target);
  const parent = dirname(resolved);
  const existing = nearestExisting(parent);
  const realCwd = await realpath(cwd);
  const realExisting = await realpath(existing);
  if (realExisting !== realCwd && !realExisting.startsWith(realCwd + sep)) {
    throw usageError('--write must be inside the current working directory');
  }
  // The real path of the target: the existing ancestor resolved through symlinks plus
  // the not-yet-created tail, so `wrote <path>` is correct even through an alias.
  const realTarget = join(realExisting, relative(existing, resolved));
  await mkdir(dirname(realTarget), { recursive: true });
  let handle;
  try {
    handle = await open(realTarget, 'wx', 0o644);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'EEXIST') {
      throw usageError('file exists');
    }
    throw error;
  }
  try {
    await handle.writeFile(text, 'utf8');
  } finally {
    await handle.close();
  }
  return relative(realCwd, realTarget);
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

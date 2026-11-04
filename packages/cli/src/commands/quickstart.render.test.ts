// `evidgate quickstart` — the rendered GitHub Actions workflow and the generic POSIX shell
// recipe: the pinned CLI version, the project guard and the no-network guarantee. The YAML
// is parsed with `yaml`, so assertions run against the document, not a substring of it.
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { beforeEach, describe, expect, it } from 'vitest';
import { parse } from 'yaml';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';
import { readCliVersion } from '../version.js';

import { capture } from './cliSpec.testing.js';
import { resolveCommand } from './index.js';
import { renderQuickstart } from './quickstart.js';
import { quickstartEnv, sampleProject, version } from './quickstart.testing.js';

interface Job {
  readonly 'runs-on': string;
  readonly if?: string;
  readonly needs?: ReadonlyArray<string>;
  readonly environment?: string;
  readonly permissions?: Record<string, string>;
  readonly env?: Record<string, string>;
  readonly steps: ReadonlyArray<Record<string, unknown>>;
}

interface Workflow {
  readonly on: Record<string, unknown>;
  readonly permissions: Record<string, string>;
  readonly jobs: Record<string, Job>;
}

function githubWorkflow(project = sampleProject): Workflow {
  return parse(renderQuickstart({ format: 'github', project, version })) as Workflow;
}

/** Visits every key/value pair of a parsed YAML document, arrays included. */
function walk(node: unknown, visit: (key: string | undefined, value: unknown) => void): void {
  if (Array.isArray(node)) {
    for (const item of node) {
      walk(item, visit);
    }
    return;
  }
  if (typeof node === 'object' && node !== null) {
    for (const [key, value] of Object.entries(node)) {
      visit(key, value);
      walk(value, visit);
    }
  }
}

describe('quickstart', () => {
  beforeEach(() => {
    resetSecrets();
  });

  // The rendered workflow parses, grants nothing at the top level, and the token-minting
  // job elevates per job with the API URL as a repository VARIABLE.
  it('renders a GitHub workflow that parses with the expected permissions and env', () => {
    const workflow = githubWorkflow();
    expect(workflow.permissions).toEqual({});
    const test = workflow.jobs.test;
    expect(test?.['runs-on']).toBe('ubuntu-24.04');
    expect(test?.permissions).toEqual({ contents: 'read' });
    const upload = workflow.jobs.upload;
    expect(upload?.['runs-on']).toBe('ubuntu-24.04');
    expect(upload?.environment).toBe('evidgate-upload');
    expect(upload?.permissions).toEqual({ contents: 'read', 'id-token': 'write' });
    expect(upload?.env?.EVIDGATE_API_URL).toBe('${{ vars.EVIDGATE_API_URL }}');
    expect(upload?.env?.EVIDGATE_PROJECT).toBe(sampleProject);
  });

  // The rendered workflow is fork-safe: the default grants no `id-token`, and the
  // token-minting job is never triggered by a pull_request event.
  it('keeps the token out of the workflow default and off pull_request', () => {
    const workflow = githubWorkflow();
    expect(workflow.permissions['id-token']).toBeUndefined();
    expect(Object.keys(workflow.on)).not.toContain('pull_request_target');
    expect(Object.keys(workflow.on)).not.toContain('workflow_run');
    const minting = Object.entries(workflow.jobs).filter(
      ([, job]) => job.permissions?.['id-token'] === 'write',
    );
    expect(minting).toHaveLength(1);
    const [name, job] = minting[0] ?? [];
    expect(name).toBe('upload');
    expect(job?.if).toBeDefined();
    expect(job?.if).not.toContain('pull_request');
    expect(job?.if).toContain("github.event_name == 'workflow_dispatch'");
    expect(job?.if).toContain("github.event_name == 'push'");
    // The workflow_dispatch path is restricted to the default branch too, so a dispatch
    // from a branch carrying an edited copy of the workflow cannot mint.
    expect(job?.if).toContain(
      "(github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main')",
    );
  });

  // No stored secret and no token-shaped value anywhere in either render.
  it('never renders a stored secret or a token-shaped value', () => {
    const forbiddenKey = /token|secret|password|key/i;
    // `id-token` is a permission name, not a credential (the only allowed match).
    const allowedKeys: Record<string, true> = { 'id-token': true };
    const tokenShape = /egt_|eyJ|[A-Za-z0-9+/=_-]{32,}/;

    walk(githubWorkflow(), (key, value) => {
      if (key !== undefined) {
        expect(forbiddenKey.test(key) && allowedKeys[key] !== true, `forbidden key: ${key}`).toBe(
          false,
        );
      }
      if (typeof value === 'string') {
        expect(value.includes('secrets.'), `secrets. in value: ${value}`).toBe(false);
        // The `uses:` pins are 40-hex SHAs by design (asserted below), so they are the
        // one place a long hex run is expected.
        if (key !== 'uses') {
          expect(tokenShape.test(value), `token-shaped value: ${value}`).toBe(false);
        }
      }
    });

    const usesValues = Object.values(githubWorkflow().jobs).flatMap((job) =>
      job.steps.flatMap((step) => (typeof step.uses === 'string' ? [step.uses] : [])),
    );
    expect(usesValues).toHaveLength(4);
    for (const uses of usesValues) {
      expect(uses).toMatch(/^[\w.-]+\/[\w.-]+@[0-9a-f]{40}$/);
    }

    const generic = renderQuickstart({ format: 'generic', project: sampleProject, version });
    expect(generic.includes('secrets.')).toBe(false);
    for (const line of generic.split('\n')) {
      expect(/EVIDGATE_(INGEST|OIDC)_TOKEN\s*=/.test(line), `token assignment: ${line}`).toBe(
        false,
      );
    }
  });

  // The CLI pin is the exact package version, never a range or `latest`.
  it('pins the CLI to the exact package version', () => {
    expect(readCliVersion()).toBe(version);
    for (const format of ['github', 'generic'] as const) {
      const text = renderQuickstart({ format, project: sampleProject, version });
      const pins = [...text.matchAll(/@evidgate\/cli@([^\s]+)/g)].map((match) => match[1]);
      expect(pins.length).toBeGreaterThan(0);
      for (const pin of pins) {
        expect(pin).toBe(version);
        expect(pin).toMatch(/^\d+\.\d+\.\d+$/);
      }
    }
  });

  // Every non-comment invocation resolves in the real registry, and no unbuilt-step
  // placeholder may survive in either render.
  it('invokes only registered commands and keeps unbuilt steps in comments', () => {
    for (const format of ['github', 'generic'] as const) {
      const text = renderQuickstart({ format, project: sampleProject, version });
      const lines = text.split('\n');
      for (const line of lines) {
        if (line.trimStart().startsWith('#')) {
          continue;
        }
        const match = /-- evidgate ([a-z]+(?: [a-z]+)*)/.exec(line);
        if (match === null) {
          continue;
        }
        const command = match[1] ?? '';
        const resolved = resolveCommand(command.split(' '));
        expect(resolved, `unregistered command: ${command}`).toBeDefined();
        expect(resolved?.command.name).toBe(command);
        expect(resolved?.rest).toEqual([]);
      }
      // Every step is real: `runs create`, `events send`/`runs finalize` and `artifacts
      // upload`, so no "-- Not yet available" placeholder may remain.
      for (const line of lines) {
        expect(line.includes('Not yet available'), `stale placeholder: ${line}`).toBe(false);
      }
    }
  });

  // `artifacts upload` requires `--execution-key` (only the manifest knows the planned
  // execution), so the rendered steps must pass it — a command that exits 3 on a missing
  // flag would make the template a non-working recipe.
  it('passes the required execution key to artifacts upload', () => {
    const github = renderQuickstart({ format: 'github', project: sampleProject, version });
    const generic = renderQuickstart({ format: 'generic', project: sampleProject, version });

    const githubSteps = github
      .split('\n')
      .filter((line) => /-- evidgate artifacts upload /.test(line));
    expect(githubSteps).toHaveLength(2);
    for (const line of githubSteps) {
      expect(line).toContain('--execution-key "$EVIDGATE_EXECUTION"');
    }
    expect(github).toContain('          EVIDGATE_EXECUTION: ${{ vars.EVIDGATE_EXECUTION }}\n');

    const genericSteps = generic
      .split('\n')
      .filter((line) => /-- evidgate artifacts upload /.test(line));
    expect(genericSteps).toHaveLength(1);
    expect(genericSteps[0]).toContain('--execution-key "$EVIDGATE_EXECUTION"');
    // The script refuses to run without the variable instead of uploading under a wrong
    // (or empty) execution.
    expect(generic).toContain(
      ': "${EVIDGATE_EXECUTION:?set EVIDGATE_EXECUTION to the planned execution this shard ran}"\n',
    );
  });

  // The id/hash reach the command: the GitHub template through masked step outputs
  // (per R4), the generic one through shell variables from the captured `--json` output.
  // `run_id` is captured the same way because the spool steps take it as `--run`.
  it('renders the pinned runs create step and no runs-create placeholder', () => {
    const npxPin = `npx --yes --package @evidgate/cli@${version}`;
    const github = renderQuickstart({ format: 'github', project: sampleProject, version });
    const generic = renderQuickstart({ format: 'generic', project: sampleProject, version });
    for (const text of [github, generic]) {
      expect(text).not.toContain('runs create (04.2.2-T2)');
    }
    expect(github).toContain(
      `          out=$(${npxPin} -- evidgate runs create --manifest-id "$MANIFEST_ID" --manifest-hash "$MANIFEST_HASH" --ci-ref "$GITHUB_REF" --json)\n`,
    );
    expect(github).toContain('          run_id=$(printf \'%s\' "$out" | jq -r .run_id)\n');
    expect(github).toContain('          echo "::add-mask::$run_id"\n');
    expect(github).toContain('          echo "run_id=$run_id" >> "$GITHUB_OUTPUT"\n');
    expect(github).toContain(
      `          out=$(${npxPin} -- evidgate manifests upload --file evidgate-manifest.json --json)\n`,
    );
    expect(github).toContain('          echo "manifest_id=$manifest_id" >> "$GITHUB_OUTPUT"\n');
    expect(github).toContain('          MANIFEST_ID: ${{ steps.manifest.outputs.manifest_id }}\n');
    expect(generic).toContain(
      `out=$(${npxPin} -- evidgate manifests upload --file evidgate-manifest.json --json)\n`,
    );
    expect(generic).toContain(
      `manifest_id=$(node -p 'JSON.parse(process.argv[1]).manifest_id' "$out")\n`,
    );
    expect(generic).toContain(
      `out=$(${npxPin} -- evidgate runs create --manifest-id "$manifest_id" --manifest-hash "$manifest_hash" --json)\n`,
    );
    expect(generic).toContain(`run_id=$(node -p 'JSON.parse(process.argv[1]).run_id' "$out")\n`);
  });

  // The spool steps are real commands in that order, and both read the same
  // EVIDGATE_SPOOL_DIR the reporter writes (contracts/cli.md §11/§10).
  it('renders the pinned events send and runs finalize steps', () => {
    const npxPin = `npx --yes --package @evidgate/cli@${version}`;
    const github = renderQuickstart({ format: 'github', project: sampleProject, version });
    const generic = renderQuickstart({ format: 'generic', project: sampleProject, version });

    // Every placeholder is gone: the three spool/upload commands are real steps.
    for (const text of [github, generic]) {
      expect(text).not.toContain('events send + runs finalize (04.2.3-T1/T2)');
      expect(text).not.toContain('artifacts upload (04.2.4-T1)');
      expect(text).toContain('artifacts upload --run');
    }

    // GitHub: the run id comes from the create step's output, the spool dir is absolute.
    expect(github).toContain(`        run: ${npxPin} -- evidgate events send --run "$RUN_ID"\n`);
    expect(github).toContain(
      `        run: ${npxPin} -- evidgate runs finalize --run "$RUN_ID" --producer shard-1 --shard 1 --manifest-hash "$MANIFEST_HASH"\n`,
    );
    expect(github).toContain(
      '          EVIDGATE_SPOOL_DIR: ${{ github.workspace }}/.evidgate-spool\n',
    );
    // The finalize step is the last one and runs after the event step.
    expect(github.indexOf('evidgate events send')).toBeLessThan(
      github.indexOf('evidgate runs finalize'),
    );
    expect(github.indexOf('evidgate runs create')).toBeLessThan(
      github.indexOf('evidgate events send'),
    );

    // Generic: the same order, from the shell variables the `--json` output produced.
    expect(generic).toContain(`${npxPin} -- evidgate events send --run "$run_id"\n`);
    expect(generic).toContain(
      `${npxPin} -- evidgate runs finalize --run "$run_id" --producer shard-1 --shard 1 --manifest-hash "$manifest_hash"\n`,
    );
    expect(generic).toContain('EVIDGATE_SPOOL_DIR="${EVIDGATE_SPOOL_DIR:-$PWD/.evidgate-spool}"\n');
    expect(generic).toContain('export EVIDGATE_SPOOL_DIR\n');
    expect(generic.indexOf('evidgate runs create')).toBeLessThan(
      generic.indexOf('evidgate events send'),
    );
    expect(generic.indexOf('evidgate events send')).toBeLessThan(
      generic.indexOf('evidgate runs finalize'),
    );
  });

  // A project that could break out of the YAML or shell template is refused, and the
  // value is never echoed.
  it('refuses a project that cannot be rendered in a CI template', async () => {
    for (const project of ['${{ github.token }}', 'a b', "x'y", 'a:b', '-foo']) {
      const { io, stdout, stderr } = capture();
      await expect(main(['quickstart'], quickstartEnv(project), io)).resolves.toBe(3);
      expect(stdout.join('')).toBe('');
      expect(stderr.join('')).toContain('invalid_config');
      expect(stderr.join('')).not.toContain(project);
    }
  });

  // `--format` accepts only the two documented values, and `generic` is a shell script.
  it('rejects an unknown --format and renders the generic script for generic', async () => {
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--format', 'gitlab'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('--format must be github or generic');
    expect(stdout.join('')).toBe('');

    const generic = renderQuickstart({ format: 'generic', project: sampleProject, version });
    expect(generic.startsWith('#!/bin/sh\n')).toBe(true);
    expect(generic).toContain('set -eu');

    const viaMain = capture();
    await expect(
      main(['quickstart', '--format', 'generic'], quickstartEnv(sampleProject), viaMain.io),
    ).resolves.toBe(0);
    expect(viaMain.stdout.join('')).toBe(generic);
  });

  // The generic recipe exports EVIDGATE_API_URL right after the `:?` guard, so the
  // pinned `npx` steps inherit it.
  it('exports EVIDGATE_API_URL in the generic recipe after the guard', () => {
    const lines = renderQuickstart({ format: 'generic', project: sampleProject, version }).split(
      '\n',
    );
    const guard = lines.findIndex((line) => line.startsWith(': "${EVIDGATE_API_URL:?'));
    expect(guard).toBeGreaterThanOrEqual(0);
    expect(lines[guard + 1]).toBe('export EVIDGATE_API_URL');
  });

  // Without `--write` the template is the only thing on stdout.
  it('prints the template to stdout and nothing else', async () => {
    const { io, stdout, stderr } = capture();
    await expect(main(['quickstart'], quickstartEnv(sampleProject), io)).resolves.toBe(0);
    expect(stdout.join('')).toBe(
      renderQuickstart({ format: 'github', project: sampleProject, version }),
    );
    expect(stderr.join('')).toBe('');
  });

  // `quickstart` is a local template: it must not touch the network.
  it('never touches the network', async () => {
    const { io, stdout, stderr } = capture();
    const fetchImpl: typeof fetch = () => {
      throw new Error('network must not be used');
    };
    await expect(
      main(['quickstart'], quickstartEnv(sampleProject), io, { fetch: fetchImpl }),
    ).resolves.toBe(0);
    expect(stdout.join('')).toBe(
      renderQuickstart({ format: 'github', project: sampleProject, version }),
    );
    expect(stderr.join('')).toBe('');
  });

  // Drift guard: the committed examples are exactly what the command renders.
  it('matches the committed docs/quickstart examples byte-for-byte', async () => {
    const docs = fileURLToPath(new URL('../../../../docs/quickstart/', import.meta.url));
    expect(await readFile(join(docs, 'github-actions.yml'), 'utf8')).toBe(
      renderQuickstart({ format: 'github', project: sampleProject, version }),
    );
    expect(await readFile(join(docs, 'generic.sh'), 'utf8')).toBe(
      renderQuickstart({ format: 'generic', project: sampleProject, version }),
    );
  });
});

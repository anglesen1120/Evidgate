// 04.2.4-T2.1: `evidgate quickstart` — the rendered GitHub Actions workflow and the
// generic POSIX shell recipe, the pinned CLI version, the project guard, `--write` path
// safety and the no-network guarantee (04.2.4 §2 AC5, §5, §8, §9, §10; contracts/cli.md
// §1/§5). The rendered YAML is parsed with the `yaml` package so the assertions run
// against the real document, not a substring of the text.
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, readFile, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { parse } from 'yaml';

import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { readCliVersion } from '../version.js';

import { resolveCommand } from './index.js';
import { renderQuickstart } from './quickstart.js';

/** The exact version the template must pin, read from the manifest, not from the code. */
const packageJson = JSON.parse(
  readFileSync(new URL('../../package.json', import.meta.url), 'utf8'),
) as { version: string };
const version = packageJson.version;

/** The sample project the committed `docs/quickstart/` examples use. */
const sampleProject = 'my-project';

interface Workflow {
  readonly permissions: Record<string, string>;
  readonly jobs: Record<
    string,
    {
      readonly 'runs-on': string;
      readonly env: Record<string, string>;
      readonly steps: ReadonlyArray<Record<string, unknown>>;
    }
  >;
}

interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

/** The minimal environment `quickstart` needs: an API URL and a project, no token. */
function quickstartEnv(project: string): Record<string, string> {
  return { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: project };
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

  // AC5 / §9: the rendered workflow parses and grants exactly the OIDC permission the
  // exchange needs, with the API URL as a repository VARIABLE and the project literal.
  it('renders a GitHub workflow that parses with the expected permissions and env', () => {
    const workflow = githubWorkflow();
    expect(workflow.permissions).toEqual({ contents: 'read', 'id-token': 'write' });
    const job = workflow.jobs.evidgate;
    expect(job?.['runs-on']).toBe('ubuntu-24.04');
    expect(job?.env.EVIDGATE_API_URL).toBe('${{ vars.EVIDGATE_API_URL }}');
    expect(job?.env.EVIDGATE_PROJECT).toBe(sampleProject);
  });

  // AC5 / §8: no stored secret and no token-shaped value anywhere in either render.
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

    const usesValues = githubWorkflow().jobs.evidgate?.steps.flatMap((step) =>
      typeof step.uses === 'string' ? [step.uses] : [],
    );
    expect(usesValues).toHaveLength(2);
    for (const uses of usesValues ?? []) {
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

  // AC5: the CLI pin is the exact package version, never a range or `latest`.
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

  // §9: every non-comment invocation resolves in the real registry, and the unbuilt
  // steps stay in comments naming their owning task.
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
      for (const line of lines) {
        for (const placeholder of ['runs create', 'runs finalize', 'artifacts upload']) {
          if (!line.includes(placeholder)) {
            continue;
          }
          expect(line.trimStart().startsWith('#'), `placeholder not in a comment: ${line}`).toBe(
            true,
          );
          expect(/04\.2\.[234]/.test(line), `placeholder without owning task: ${line}`).toBe(true);
        }
        if (!line.trimStart().startsWith('#')) {
          expect(/evidgate (runs|events|artifacts)\b/.test(line), `fake command: ${line}`).toBe(
            false,
          );
        }
      }
    }
  });

  // AC5: a project that could break out of the YAML or shell template is refused, and
  // the value is never echoed.
  it('refuses a project that cannot be rendered in a CI template', async () => {
    for (const project of ['${{ github.token }}', 'a b', "x'y", 'a:b', '-foo']) {
      const { io, stdout, stderr } = capture();
      await expect(main(['quickstart'], quickstartEnv(project), io)).resolves.toBe(3);
      expect(stdout.join('')).toBe('');
      expect(stderr.join('')).toContain('invalid_config');
      expect(stderr.join('')).not.toContain(project);
    }
  });

  // AC5: `--format` accepts only the two documented values; `generic` is a shell script.
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

  // AC5: without `--write` the template is the only thing on stdout.
  it('prints the template to stdout and nothing else', async () => {
    const { io, stdout, stderr } = capture();
    await expect(main(['quickstart'], quickstartEnv(sampleProject), io)).resolves.toBe(0);
    expect(stdout.join('')).toBe(
      renderQuickstart({ format: 'github', project: sampleProject, version }),
    );
    expect(stderr.join('')).toBe('');
  });

  // AC5: `quickstart` is a local template — it must not touch the network.
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

  // §9 drift guard: the committed examples are exactly what the command renders.
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

describe('quickstart --write', () => {
  const dirs: string[] = [];
  let originalCwd = '';

  beforeEach(async () => {
    originalCwd = process.cwd();
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-quickstart-'));
    dirs.push(dir);
    process.chdir(dir);
  });

  afterEach(async () => {
    process.chdir(originalCwd);
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  // AC5: `--write` creates the file (mode 0644) and any missing parent directories.
  it('writes the rendered template, creating parent directories', async () => {
    const printed = capture();
    await expect(main(['quickstart'], quickstartEnv(sampleProject), printed.io)).resolves.toBe(0);
    const expected = printed.stdout.join('');

    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'ci/evidgate.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(0);
    expect(stdout.join('')).toBe('wrote ci/evidgate.yml\n');
    expect(stderr.join('')).toBe('');

    const target = join(process.cwd(), 'ci/evidgate.yml');
    expect(await readFile(target, 'utf8')).toBe(expected);
    expect((await stat(target)).mode & 0o777).toBe(0o644);
  });

  // AC5: the exclusive `wx` flag never overwrites an existing file.
  it('refuses to overwrite an existing file', async () => {
    await writeFile(join(process.cwd(), 'out.yml'), 'keep me', 'utf8');
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'out.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('file exists');
    expect(stdout.join('')).toBe('');
    expect(await readFile(join(process.cwd(), 'out.yml'), 'utf8')).toBe('keep me');
  });

  // AC5: a `..` escape is refused and nothing is created outside the cwd.
  it('refuses a path that escapes the working directory', async () => {
    const outside = join(process.cwd(), '..', 'quickstart-outside.yml');
    const { io, stdout, stderr } = capture();
    await expect(
      main(
        ['quickstart', '--write', '../quickstart-outside.yml'],
        quickstartEnv(sampleProject),
        io,
      ),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('inside the current working directory');
    expect(stdout.join('')).toBe('');
    expect(existsSync(outside)).toBe(false);
  });

  // AC5: a symlinked parent that points outside the cwd is refused by the realpath check.
  it('refuses a symlinked parent that points outside the working directory', async () => {
    const outside = await mkdtemp(join(tmpdir(), 'evidgate-quickstart-outside-'));
    dirs.push(outside);
    await symlink(outside, join(process.cwd(), 'link'), 'dir');
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'link/out.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('inside the current working directory');
    expect(stdout.join('')).toBe('');
    expect(existsSync(join(outside, 'out.yml'))).toBe(false);
  });
});

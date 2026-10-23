// Independent QA for 04.2.4-T2 (AC5, contracts/cli.md §10).
import { existsSync, readFileSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { parse } from 'yaml';

import { main } from '../main.js';

import { renderQuickstart } from './quickstart.js';

const packageJson: unknown = JSON.parse(
  readFileSync(new URL('../../package.json', import.meta.url), 'utf8'),
);
if (
  packageJson === null ||
  typeof packageJson !== 'object' ||
  !('version' in packageJson) ||
  typeof packageJson.version !== 'string'
) {
  throw new Error('CLI package version must be a string');
}
const version = packageJson.version;
const env = { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: 'my-project' };

function capture() {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    stdout,
    stderr,
    io: {
      stdout: (text: string) => stdout.push(text),
      stderr: (text: string) => stderr.push(text),
    },
  };
}

describe('quickstart independent QA', () => {
  it.each(['a'.repeat(128), 'a._-9', '0'])(
    'renders the valid project boundary %s',
    async (project) => {
      const output = capture();
      expect(await main(['quickstart'], { ...env, EVIDGATE_PROJECT: project }, output.io)).toBe(0);
      const workflow = parse(output.stdout.join('')) as {
        jobs: { upload: { env: { EVIDGATE_PROJECT: string } } };
      };
      expect(workflow.jobs.upload.env.EVIDGATE_PROJECT).toBe(project);
      expect(output.stderr.join('')).toBe('');
    },
  );

  it.each(['a'.repeat(129), '.foo', '_foo', '-foo', 'éfoo', '${{github.token}}'])(
    'refuses unsafe project %s without echo',
    async (project) => {
      const output = capture();
      expect(await main(['quickstart'], { ...env, EVIDGATE_PROJECT: project }, output.io)).toBe(3);
      expect(output.stdout.join('')).toBe('');
      expect(output.stderr.join('')).toContain('invalid_config');
      expect(output.stderr.join('')).not.toContain(project);
    },
  );

  it('lists both missing required settings without requesting a token source', async () => {
    const output = capture();
    expect(await main(['quickstart'], {}, output.io)).toBe(3);
    expect(output.stdout.join('')).toBe('');
    expect(output.stderr.join('')).toContain('missing_config');
    expect(output.stderr.join('')).toContain('EVIDGATE_API_URL');
    expect(output.stderr.join('')).toContain('EVIDGATE_PROJECT');
    expect(output.stderr.join('')).not.toContain('a token source');
  });

  it.each([
    ['github', 'generic'],
    ['generic', 'github'],
  ] as const)('uses the last repeated format (%s then %s)', async (first, last) => {
    const output = capture();
    expect(await main(['quickstart', '--format', first, '--format', last], env, output.io)).toBe(0);
    expect(output.stdout.join('')).toBe(
      renderQuickstart({ format: last, project: env.EVIDGATE_PROJECT, version }),
    );
    expect(output.stderr.join('')).toBe('');
  });

  it.each([['--format='], ['--format'], ['--write=']])(
    'refuses empty or missing option %s',
    async (...args) => {
      const output = capture();
      expect(await main(['quickstart', ...args], env, output.io)).toBe(3);
      expect(output.stdout.join('')).toBe('');
      expect(output.stderr.join('')).toContain('usage');
      expect(output.stderr.join('')).toContain("run 'evidgate --help' for usage");
    },
  );

  it.each([['--json'], ['--verbose'], ['--json', '--verbose']])(
    'keeps stdout exactly the template with flags %s',
    async (...args) => {
      const output = capture();
      expect(await main(['quickstart', ...args], env, output.io)).toBe(0);
      expect(output.stdout.join('')).toBe(
        renderQuickstart({ format: 'github', project: env.EVIDGATE_PROJECT, version }),
      );
    },
  );

  // QA-01: main prints resolved project before quickstart's guard under --verbose.
  it('never echoes an invalid project with json and verbose enabled', async () => {
    const output = capture();
    const project = '${{github.token}}';
    expect(
      await main(
        ['quickstart', '--json', '--verbose'],
        { ...env, EVIDGATE_PROJECT: project },
        output.io,
      ),
    ).toBe(3);
    expect(output.stdout.join('')).toBe('');
    expect(output.stderr.join('')).not.toContain(project);
  });

  it('keeps every action SHA identical to the existing repository CI workflow', () => {
    const workflow = parse(
      renderQuickstart({ format: 'github', project: env.EVIDGATE_PROJECT, version }),
    ) as { jobs: Record<string, { steps: { uses?: string }[] }> };
    const ci = readFileSync(
      new URL('../../../../.github/workflows/ci.yml', import.meta.url),
      'utf8',
    );
    const pins = [...ci.matchAll(/uses:\s*([^\s#]+)/g)].map((match) => match[1]);
    const actions = Object.values(workflow.jobs).flatMap((job) =>
      job.steps.flatMap((step) => (step.uses ? [step.uses] : [])),
    );
    expect(actions).toHaveLength(4);
    for (const action of actions) expect(pins).toContain(action);
  });
});

describe('quickstart file and OIDC cache QA', () => {
  let originalCwd: string;
  let cwd: string;
  const dirs: string[] = [];
  beforeEach(async () => {
    originalCwd = process.cwd();
    cwd = await mkdtemp(join(tmpdir(), 'evidgate-qa-'));
    dirs.push(cwd);
    process.chdir(cwd);
  });
  afterEach(async () => {
    process.chdir(originalCwd);
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  it('accepts an absolute canonical path inside cwd', async () => {
    const output = capture();
    const target = join(process.cwd(), 'absolute.yml');
    expect(await main(['quickstart', '--write', target], env, output.io)).toBe(0);
    expect(await readFile(target, 'utf8')).toBe(
      renderQuickstart({ format: 'github', project: env.EVIDGATE_PROJECT, version }),
    );
    expect(output.stdout.join('')).toBe('wrote absolute.yml\n');
  });

  // QA-02: macOS /tmp -> /private/tmp loses the caller's absolute cwd spelling.
  it('accepts an absolute path using the caller cwd symlink spelling', async () => {
    const aliasRoot = await mkdtemp(join(tmpdir(), 'evidgate-qa-alias-'));
    dirs.push(aliasRoot);
    const alias = join(aliasRoot, 'cwd');
    await symlink(cwd, alias, 'dir');
    process.chdir(alias);
    const output = capture();
    expect(await main(['quickstart', '--write', join(alias, 'absolute.yml')], env, output.io)).toBe(
      0,
    );
    expect(await readFile(join(cwd, 'absolute.yml'), 'utf8')).toContain('name: evidgate');
  });

  it.each(['regular', 'symlink', 'dangling', 'directory'])(
    'never replaces an existing %s target',
    async (kind) => {
      const target = join(cwd, 'out');
      const referenced = join(cwd, 'referenced');
      if (kind === 'regular') await writeFile(target, 'keep');
      if (kind === 'symlink') {
        await writeFile(referenced, 'keep');
        await symlink(referenced, target);
      }
      if (kind === 'dangling') await symlink(referenced, target);
      if (kind === 'directory') await mkdir(target);
      const output = capture();
      expect(await main(['quickstart', '--write', target], env, output.io)).toBe(3);
      expect(output.stdout.join('')).toBe('');
      expect(output.stderr.join('')).toContain('file exists');
      if (kind === 'regular') expect(await readFile(target, 'utf8')).toBe('keep');
      if (kind === 'symlink') expect(await readFile(referenced, 'utf8')).toBe('keep');
      if (kind === 'dangling') expect(existsSync(referenced)).toBe(false);
      if (kind === 'directory') expect((await stat(target)).isDirectory()).toBe(true);
    },
  );

  it('allows a symlinked parent whose real directory remains inside cwd', async () => {
    await mkdir(join(cwd, 'real'));
    await symlink(join(cwd, 'real'), join(cwd, 'link'), 'dir');
    const output = capture();
    expect(
      await main(
        ['quickstart', '--write', 'link/nested/out.sh', '--format', 'generic'],
        env,
        output.io,
      ),
    ).toBe(0);
    expect(await readFile(join(cwd, 'real/nested/out.sh'), 'utf8')).toBe(
      renderQuickstart({ format: 'generic', project: env.EVIDGATE_PROJECT, version }),
    );
  });

  it('exchanges the Actions JWT once, then uploads with the cached token without github-env', async () => {
    const cache = join(cwd, 'cache');
    const authEnv = {
      ...env,
      EVIDGATE_CACHE_DIR: cache,
      ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.evidgate.test/token',
      ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'qa-request-credential',
    };
    const calls: { url: string; authorization: string | null; method: string }[] = [];
    const fetchImpl: typeof fetch = (input, init) => {
      const request = new Request(input, init);
      calls.push({
        url: request.url,
        authorization: request.headers.get('authorization'),
        method: request.method,
      });
      let body: unknown;
      if (request.url.startsWith('https://actions.evidgate.test/')) body = { value: 'qa-oidc-jwt' };
      else if (request.url.endsWith('/v1/ci/token'))
        body = {
          token: 'qa-producer-token',
          expires_at: new Date(Date.now() + 600_000).toISOString(),
          project_id: '11111111-1111-4111-8111-111111111111',
          permission_set: ['manifest:write'],
        };
      else if (request.url.endsWith('/v1/projects/my-project/manifests'))
        body = {
          manifest_id: '22222222-2222-4222-8222-222222222222',
          manifest_hash: 'sha256:' + 'a'.repeat(64),
        };
      else return Promise.reject(new Error('unexpected request'));
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      );
    };
    const login = capture();
    expect(await main(['auth', 'login'], authEnv, login.io, { fetch: fetchImpl })).toBe(0);
    await writeFile(join(cwd, 'manifest.json'), '{}');
    const upload = capture();
    expect(
      await main(
        ['manifests', 'upload', '--file', join(cwd, 'manifest.json')],
        authEnv,
        upload.io,
        { fetch: fetchImpl },
      ),
    ).toBe(0);
    expect(calls).toEqual([
      {
        url: 'https://actions.evidgate.test/token?audience=evidgate',
        authorization: 'bearer qa-request-credential',
        method: 'GET',
      },
      {
        url: 'https://api.evidgate.test/v1/ci/token',
        authorization: 'Bearer qa-oidc-jwt',
        method: 'POST',
      },
      {
        url: 'https://api.evidgate.test/v1/projects/my-project/manifests',
        authorization: 'Bearer qa-producer-token',
        method: 'POST',
      },
    ]);
    const output = [...login.stdout, ...login.stderr, ...upload.stdout, ...upload.stderr].join('');
    for (const secret of ['qa-request-credential', 'qa-oidc-jwt', 'qa-producer-token'])
      expect(output).not.toContain(secret);
  });
});

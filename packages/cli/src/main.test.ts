import { beforeEach, describe, expect, it } from 'vitest';

import { main } from './main.js';
import { resetSecrets, type Io } from './redact.js';

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

const baseEnv = {
  EVIDGATE_API_URL: 'https://api.evidgate.test',
  EVIDGATE_PROJECT: 'proj-1',
};

const devEnv = { ...baseEnv, EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' };

// 04.2.1-T2.1/T2.2: the dispatcher — help/version, the AC5 missing-config exit, the
// AC8 dev path, the `--json` shape and redaction over every error path (T78).
describe('main', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('prints usage and exits 0 for --help, -h and the help command', async () => {
    for (const argv of [['--help'], ['-h'], ['help']]) {
      const { io, stdout } = capture();
      await expect(main(argv, {}, io)).resolves.toBe(0);
      expect(stdout.join('')).toContain('Usage: evidgate');
    }
  });

  it('prints the package version and exits 0 for --version', async () => {
    const { io, stdout } = capture();
    await expect(main(['--version'], {}, io)).resolves.toBe(0);
    expect(stdout.join('').trim()).toMatch(/^\d+\.\d+\.\d+/);
  });

  it('exits 3 naming every missing variable for config check with an empty env (AC5)', async () => {
    const { io, stderr } = capture();
    await expect(main(['config', 'check'], {}, io)).resolves.toBe(3);
    const text = stderr.join('');
    expect(text).toContain('evidgate: error missing_config:');
    expect(text).toContain('EVIDGATE_API_URL (or --api-url)');
    expect(text).toContain('EVIDGATE_PROJECT (or --project)');
    expect(text).toContain('EVIDGATE_OIDC_TOKEN');
    expect(text).toContain('ACTIONS_ID_TOKEN_REQUEST_URL + ACTIONS_ID_TOKEN_REQUEST_TOKEN');
    expect(text).toContain('EVIDGATE_INGEST_TOKEN');
    expect(text).toContain('EVIDGATE_DEV_TOKEN');
  });

  it('runs config check on the dev path and never prints the token (AC8, T78)', async () => {
    const { io, stdout, stderr } = capture();
    await expect(main(['config', 'check'], devEnv, io)).resolves.toBe(0);
    expect(stdout.join('')).toBe(
      'ok api_url=https://api.evidgate.test project=proj-1 token_source=dev\n',
    );
    expect(stdout.join('')).not.toContain('dev-token-value');
    expect(stderr.join('')).not.toContain('dev-token-value');
  });

  it('emits the --json shape without a token', async () => {
    const { io, stdout } = capture();
    await expect(main(['config', 'check', '--json'], devEnv, io)).resolves.toBe(0);
    const payload = JSON.parse(stdout.join('')) as {
      ok: boolean;
      api_url: string;
      project: string;
      token_source: string;
    };
    expect(payload).toEqual({
      ok: true,
      api_url: 'https://api.evidgate.test',
      project: 'proj-1',
      token_source: 'dev',
    });
    expect(stdout.join('')).not.toContain('dev-token-value');
  });

  it('redacts the token in --verbose output', async () => {
    const { io, stderr } = capture();
    await expect(main(['config', 'check', '--verbose'], devEnv, io)).resolves.toBe(0);
    expect(stderr.join('')).toContain('dev_token=[REDACTED]');
    expect(stderr.join('')).not.toContain('dev-token-value');
  });

  it('exits 3 with usage for an unknown flag', async () => {
    const { io, stderr } = capture();
    await expect(main(['config', 'check', '--nope'], {}, io)).resolves.toBe(3);
    expect(stderr.join('')).toContain('evidgate: error usage:');
    expect(stderr.join('')).toContain("run 'evidgate --help' for usage");
  });

  it('exits 3 with usage for a flag missing its value', async () => {
    const { io, stderr } = capture();
    await expect(main(['config', 'check', '--api-url'], {}, io)).resolves.toBe(3);
    expect(stderr.join('')).toContain('evidgate: error usage:');
  });

  it('exits 3 with usage for an unknown command', async () => {
    const { io, stderr } = capture();
    await expect(main(['frobnicate'], {}, io)).resolves.toBe(3);
    expect(stderr.join('')).toContain("unknown command 'frobnicate'");
  });

  it('exits 3 with usage when no command is given', async () => {
    const { io, stderr } = capture();
    await expect(main([], {}, io)).resolves.toBe(3);
    expect(stderr.join('')).toContain('no command given');
  });

  it('redacts the token on every error path', async () => {
    const token = 'dev-token-value';
    const cases: ReadonlyArray<readonly [string[], Record<string, string | undefined>]> = [
      [['config', 'check'], { ...baseEnv, EVIDGATE_ENV: 'prod', EVIDGATE_DEV_TOKEN: token }],
      [
        ['config', 'check'],
        {
          ...baseEnv,
          EVIDGATE_ENV: 'dev',
          EVIDGATE_DEV_TOKEN: token,
          EVIDGATE_OIDC_TOKEN: 'oidc-jwt',
        },
      ],
      [
        ['config', 'check', '--api-url', 'http://api.evidgate.test'],
        { ...baseEnv, EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: token },
      ],
      [
        ['config', 'check'],
        {
          EVIDGATE_API_URL: baseEnv.EVIDGATE_API_URL,
          EVIDGATE_ENV: 'dev',
          EVIDGATE_DEV_TOKEN: token,
        },
      ],
    ];
    for (const [argv, env] of cases) {
      const { io, stdout, stderr } = capture();
      await expect(main(argv, env, io)).resolves.toBe(3);
      expect(stdout.join('')).not.toContain(token);
      expect(stderr.join('')).not.toContain(token);
    }
  });

  it('redacts an unexpected failure and its stack (internal_error)', async () => {
    const token = 'dev-token-value';
    const stderr: string[] = [];
    const io: Io = {
      stdout: () => {
        throw new Error(`unexpected failure with ${token}`);
      },
      stderr: (text) => stderr.push(text),
    };
    await expect(main(['config', 'check', '--verbose'], devEnv, io)).resolves.toBe(3);
    expect(stderr.join('')).toContain('evidgate: error internal_error:');
    expect(stderr.join('')).not.toContain(token);
  });
});

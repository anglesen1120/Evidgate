import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { tokenCachePath } from './auth/tokenCache.js';
import { ExitCode } from './exitCodes.js';
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

// The dev token is accepted only for a loopback API URL (contracts/cli.md §4), and it is a
// seeded ingest credential: `getToken` exchanges it at `POST /v1/ci/token`, so every dev-path
// run needs an answering stub and a temporary cache directory.
const devEnv: Record<string, string> = {
  EVIDGATE_API_URL: 'http://127.0.0.1:8080',
  EVIDGATE_PROJECT: 'proj-1',
  EVIDGATE_ENV: 'dev',
  EVIDGATE_DEV_TOKEN: 'dev-token-value',
  // Replaced per test by a fresh mkdtemp; never the real ~/.cache/evidgate, even if a test forgets.
  EVIDGATE_CACHE_DIR: join(tmpdir(), 'evidgate-cli-main-unset'),
};

/** Answers `POST /v1/ci/token` with a valid body. The default `token` is deliberately NOT the
 * dev token: that string is already registered by `registerEnvSecrets`, so a matching stub would
 * make every "not printed" assertion pass even if the exchanged credential were never redacted. */
function exchangeFetch(token = 'egt_exchanged_dev_token'): {
  readonly fetchImpl: typeof fetch;
  readonly requests: Request[];
  readonly expiresAt: string;
  readonly token: string;
} {
  const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
  const requests: Request[] = [];
  const fetchImpl: typeof fetch = (input, init) => {
    requests.push(input instanceof Request ? input : new Request(input, init));
    return Promise.resolve(
      new Response(
        JSON.stringify({
          token,
          expires_at: expiresAt,
          project_id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
          permission_set: [],
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      ),
    );
  };
  return { fetchImpl, requests, expiresAt, token };
}

/** A request as `METHOD /path`, so the verb the exchange uses is asserted, not only the path. */
function requestLines(requests: readonly Request[]): string[] {
  return requests.map((request) => `${request.method} ${new URL(request.url).pathname}`);
}

/** Every non-empty stderr line must carry the `evidgate: ` prefix (SEC4). */
function expectPrefixedStderr(text: string): void {
  for (const line of text.split('\n').filter((line) => line.length > 0)) {
    expect(line.startsWith('evidgate: ')).toBe(true);
  }
}

// 04.2.1-T2.1/T2.2: the dispatcher — help/version, the AC5 missing-config exit, the
// AC8 dev path, the `--json` shape and redaction over every error path (T78).
describe('main', () => {
  let cacheDir = '';

  beforeEach(async () => {
    resetSecrets();
    // A dev-path run writes the exchanged token, so it never touches the real user cache.
    cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-main-'));
    devEnv.EVIDGATE_CACHE_DIR = cacheDir;
  });

  afterEach(async () => {
    await rm(cacheDir, { recursive: true, force: true });
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
    const exchange = exchangeFetch();
    await expect(
      main(['config', 'check'], devEnv, io, { fetch: exchange.fetchImpl }),
    ).resolves.toBe(0);
    expect(requestLines(exchange.requests)).toStrictEqual(['POST /v1/ci/token']);
    // The dev credential travels in the body as `ingest_token`, never as a bearer.
    expect(exchange.requests[0]?.headers.get('authorization')).toBeNull();
    expect(stdout.join('')).toBe(
      'ok api_url=http://127.0.0.1:8080 project=proj-1 token_source=dev\n',
    );
    expect(stdout.join('')).not.toContain('dev-token-value');
    expect(stderr.join('')).not.toContain('dev-token-value');
    // The exchanged credential is a different string, so this proves it is redacted on its own.
    expect(stdout.join('')).not.toContain(exchange.token);
    expect(stderr.join('')).not.toContain(exchange.token);
  });

  it('emits the --json shape without a token', async () => {
    const { io, stdout } = capture();
    const exchange = exchangeFetch();
    await expect(
      main(['config', 'check', '--json'], devEnv, io, { fetch: exchange.fetchImpl }),
    ).resolves.toBe(0);
    expect(requestLines(exchange.requests)).toStrictEqual(['POST /v1/ci/token']);
    const payload = JSON.parse(stdout.join('')) as {
      ok: boolean;
      api_url: string;
      project: string;
      token_source: string;
    };
    expect(payload).toEqual({
      ok: true,
      api_url: 'http://127.0.0.1:8080',
      project: 'proj-1',
      token_source: 'dev',
    });
    expect(stdout.join('')).not.toContain('dev-token-value');
    expect(stdout.join('')).not.toContain(exchange.token);
  });

  // 04.2.1-T1.1 / C1: `config check` forwards `main`'s `deps` to `getToken`, so an
  // ingest source runs the exchange and fills the cache (contracts/cli.md §6/§7).
  it('runs the ingest exchange for config check and writes the cache (C1)', async () => {
    const ingestCacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-cache-'));
    const token = 'egt_ci_token_value';
    const exchange = exchangeFetch(token);
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret',
      EVIDGATE_CACHE_DIR: ingestCacheDir,
    };
    try {
      const { io, stdout, stderr } = capture();
      await expect(main(['config', 'check'], env, io, { fetch: exchange.fetchImpl })).resolves.toBe(
        0,
      );
      expect(stdout.join('')).toContain('token_source=ingest');
      expect(stdout.join('')).not.toContain(token);
      expect(stderr.join('')).not.toContain(token);
      expect(requestLines(exchange.requests)).toStrictEqual(['POST /v1/ci/token']);
      const cached = JSON.parse(
        await readFile(
          tokenCachePath({
            apiUrl: 'https://api.evidgate.test',
            project: 'proj-1',
            cacheDir: ingestCacheDir,
            ingestToken: 'egt_ingest_secret',
          }),
          'utf8',
        ),
      ) as { token: string; expires_at: string };
      expect(cached).toEqual({ token, expires_at: exchange.expiresAt });
    } finally {
      await rm(ingestCacheDir, { recursive: true, force: true });
    }
  });

  it('redacts the token in --verbose output and prefixes every line', async () => {
    const { io, stderr } = capture();
    const exchange = exchangeFetch();
    await expect(
      main(['config', 'check', '--verbose'], devEnv, io, { fetch: exchange.fetchImpl }),
    ).resolves.toBe(0);
    expect(requestLines(exchange.requests)).toStrictEqual(['POST /v1/ci/token']);
    const text = stderr.join('');
    expect(text).toContain('dev_token=[REDACTED]');
    expect(text).not.toContain('dev-token-value');
    expect(text).not.toContain(exchange.token);
    expectPrefixedStderr(text);
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

  it('exits 3 with usage for an extra positional on config check', async () => {
    const { io, stderr } = capture();
    const exchange = exchangeFetch();
    await expect(
      main(['config', 'check', 'extra'], devEnv, io, { fetch: exchange.fetchImpl }),
    ).resolves.toBe(3);
    expect(exchange.requests).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error usage:');
    expect(stderr.join('')).toContain("unexpected argument 'extra'");
  });

  it('redacts a secret wherever a message or output echoes it (T78)', async () => {
    const token = 'evg_dev_FAKE_echo_secret';
    const urlToken = 'http://127.0.0.1:8080';
    const cases: ReadonlyArray<{
      readonly argv: string[];
      readonly env: Record<string, string | undefined>;
      readonly secret: string;
      readonly code: ExitCode;
    }> = [
      // the usage error echoes the unknown command text (QA-D1: before resolveConfig)
      {
        argv: [token],
        env: { ...devEnv, EVIDGATE_DEV_TOKEN: token },
        secret: token,
        code: ExitCode.Error,
      },
      // parseArgs echoes the unknown flag name
      {
        argv: ['config', 'check', `--${token}`],
        env: { ...devEnv, EVIDGATE_DEV_TOKEN: token },
        secret: token,
        code: ExitCode.Error,
      },
      // `config check` echoes the resolved project
      {
        argv: ['config', 'check', '--project', token],
        env: { ...devEnv, EVIDGATE_DEV_TOKEN: token },
        secret: token,
        code: ExitCode.Ok,
      },
      // `config check` echoes the resolved api_url (the secret is a valid loopback URL)
      {
        argv: ['config', 'check', '--api-url', urlToken],
        env: { ...devEnv, EVIDGATE_API_URL: urlToken, EVIDGATE_DEV_TOKEN: urlToken },
        secret: urlToken,
        code: ExitCode.Ok,
      },
    ];
    for (const { argv, env, secret, code } of cases) {
      const { io, stdout, stderr } = capture();
      const exchange = exchangeFetch();
      await expect(main(argv, env, io, { fetch: exchange.fetchImpl })).resolves.toBe(code);
      // Only the two `config check` cases get past argument parsing and exchange the dev token.
      expect(requestLines(exchange.requests)).toStrictEqual(
        code === ExitCode.Ok ? ['POST /v1/ci/token'] : [],
      );
      expect(stdout.join('')).not.toContain(secret);
      expect(stderr.join('')).not.toContain(secret);
      expect(stdout.join('')).not.toContain(exchange.token);
      expect(stderr.join('')).not.toContain(exchange.token);
    }
  });

  it('redacts an unexpected failure and its stack (internal_error)', async () => {
    const token = 'dev-token-value';
    const exchange = exchangeFetch();
    const stderr: string[] = [];
    const io: Io = {
      stdout: () => {
        // Echo both credentials: the env dev token and the one the exchange minted.
        throw new Error(`unexpected failure with ${token} and ${exchange.token}`);
      },
      stderr: (text) => stderr.push(text),
    };
    await expect(
      main(['config', 'check', '--verbose'], devEnv, io, { fetch: exchange.fetchImpl }),
    ).resolves.toBe(3);
    expect(requestLines(exchange.requests)).toStrictEqual(['POST /v1/ci/token']);
    const text = stderr.join('');
    expect(text).toContain('evidgate: error internal_error:');
    expect(text).not.toContain(token);
    expect(text).not.toContain(exchange.token);
    expectPrefixedStderr(text);
  });

  it('escapes control characters so no stderr line can start with :: (SEC4)', async () => {
    const hostile = '\n::stop-commands::tok\u001b[31m';
    const { io, stderr } = capture();
    const exchange = exchangeFetch();
    await expect(main([hostile], devEnv, io, { fetch: exchange.fetchImpl })).resolves.toBe(3);
    // A usage error is decided before any credential is resolved, so no request is made.
    expect(exchange.requests).toHaveLength(0);
    const text = stderr.join('');
    expectPrefixedStderr(text);
    expect(text).not.toContain('\n::');
    expect(text).toContain('\\u000a');
    expect(text).toContain('\\u001b');
    expect(text).toContain('::stop-commands::tok');
  });
});

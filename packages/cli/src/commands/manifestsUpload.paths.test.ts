// Path handling for `evidgate manifests upload`: non-UTF-8 bytes travel verbatim with the
// digest as the idempotency key, a symlink and a relative path are followed, and the
// non-regular inputs (a device, a FIFO) are refused without a request or a hang.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import { capture, createdManifest as ok, devUploadEnv } from './manifestsUpload.testing.js';

describe('manifests upload — boundaries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-qa-'));
    dirs.push(dir);
    return dir;
  }

  async function manifestFile(content: string | Buffer): Promise<string> {
    const path = join(await tempDir(), 'manifest.json');
    await writeFile(path, content);
    return path;
  }

  /**
   * The dev path: the token is a seeded ingest credential, so every run exchanges it at
   * `POST /v1/ci/token` (and caches the result) before the upload.
   */
  async function devEnv(extra: Record<string, string> = {}) {
    return devUploadEnv(await tempDir(), extra);
  }

  // ---- boundary: non-UTF-8 bytes travel verbatim and the key is their digest ----

  it('non_utf8_bytes_sent_verbatim', async () => {
    const bytes = Buffer.from([0xff, 0xfe, 0x00, 0x01, 0x80, 0x7f, 0x0a]);
    const file = await manifestFile(bytes);
    const env = await devEnv();
    const { fetchImpl, calls } = devStubFetch(ok);
    const { io } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    expect(calls[0]?.body.equals(bytes)).toBe(true);
    expect(calls[0]?.request.headers.get('idempotency-key')).toBe(
      createHash('sha256').update(bytes).digest('hex'),
    );
  });

  // ---- path handling: symlink, device, FIFO, relative path, empty flag ----

  it('symlink_to_a_file_is_uploaded', async () => {
    const target = await manifestFile('{"schema":"manifest.v1"}');
    const link = join(await tempDir(), 'link.json');
    await symlink(target, link);
    const env = await devEnv();
    const { fetchImpl, calls } = devStubFetch(ok);
    const { io } = capture();

    await expect(
      main(['manifests', 'upload', '--file', link], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);
    expect(calls).toHaveLength(1);
  });

  it.skipIf(process.platform === 'win32')('dev_zero_is_unreadable_with_zero_requests', async () => {
    const env = await devEnv();
    const { fetchImpl, calls } = devStubFetch(ok);
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', '/dev/zero'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error manifest_unreadable: ');
    expect(calls).toHaveLength(0);
  });

  // `open(path, 'r')` on a FIFO blocks until a writer appears, so the non-regular-file
  // check never ran and the command hung. The open is `O_RDONLY | O_NONBLOCK`, which
  // returns at once on a FIFO, so the `fstat` check refuses it (contracts/cli.md §8 step 2).
  it.skipIf(process.platform === 'win32')(
    'fifo_is_refused_without_hanging',
    async () => {
      const fifo = join(await tempDir(), 'manifest.fifo');
      execFileSync('mkfifo', [fifo]);
      const env = await devEnv();
      const { fetchImpl, calls } = devStubFetch(ok);
      const { io, stderr } = capture();

      const pending = main(['manifests', 'upload', '--file', fifo], env, io, {
        fetch: fetchImpl,
      });
      // Real wall-clock timer on purpose: the hang was a blocked libuv `open()` on the
      // FIFO, not a scheduled timer, so fake timers cannot observe it. The race is a
      // bounded liveness probe, not a sleep that masks a race.
      const outcome = await Promise.race([
        pending.then(
          (code) => `settled:${code}`,
          () => 'settled:threw',
        ),
        new Promise<string>((resolve) => setTimeout(() => resolve('hung'), 1500)),
      ]);
      await pending;

      expect(outcome).toBe('settled:3');
      expect(stderr.join('')).toContain('evidgate: error manifest_unreadable: ');
      expect(calls).toHaveLength(0);
    },
    10_000,
  );

  it('empty_file_flag_is_usage_with_zero_requests', async () => {
    const env = await devEnv();
    const { fetchImpl, calls } = devStubFetch(ok);
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file='], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error usage: ');
    expect(calls).toHaveLength(0);
  });

  it('relative_path_is_uploaded', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const rel = relative(process.cwd(), file);
    expect(rel.startsWith('.')).toBe(true);
    const env = await devEnv();
    const { fetchImpl, calls } = devStubFetch(ok);
    const { io } = capture();

    await expect(
      main(['manifests', 'upload', '--file', rel], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);
    expect(calls).toHaveLength(1);
  });
});

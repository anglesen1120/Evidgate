// Path handling for `evidgate manifests upload`: non-UTF-8 bytes travel verbatim with the
// digest as the idempotency key, a symlink and a relative path are followed, and the
// empty `--file` flag is a usage error before any request.
import { createHash } from 'node:crypto';
import { symlink } from 'node:fs/promises';
import { join, relative } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import { capture, createdManifest as ok, useManifestSpec } from './manifestsUpload.testing.js';

describe('manifests upload — boundaries', () => {
  const { tempDir, manifestFile, devEnv } = useManifestSpec('evidgate-cli-qa-');

  beforeEach(() => {
    resetSecrets();
  });

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

  // ---- path handling: symlink, relative path, empty flag ----

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

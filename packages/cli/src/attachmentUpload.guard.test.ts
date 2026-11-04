// The guard in front of every request: the engine exposes the artifact-kind cap table the
// refusal itself uses, and a file outside the allow-listed directories or over its kind's cap
// is refused BEFORE any request — not even a token exchange — with a filtered reason.
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  acceptingStore,
  artifactId,
  capOf,
  digestOfFile,
  harness,
  ingestToken,
  input,
  json,
  rejection,
  sparseFile,
  useAttachmentUploadSpec,
} from './attachmentUpload.testing.js';
import { CliError } from './errors.js';
import { artifactKindMaxBytes } from './uploadPaths.js';

describe('createAttachmentUploader', () => {
  let root = '';
  const { directories, env, uploader } = useAttachmentUploadSpec((directory) => {
    root = directory;
  });

  // The engine's `kindMaxBytes` surface is the guard's own table: a caller pre-screens
  // against the same numbers the refusal would use, so the two can never drift.
  it('exposes_the_static_kind_caps_of_the_guard', () => {
    const store = harness(() => json(200, {}));
    expect(uploader(store).kindMaxBytes).toBe(artifactKindMaxBytes);
    expect(uploader(store).kindMaxBytes.trace).toBe(capOf('trace'));
  });

  // AC6: outside the allow-listed directories the guard refuses and NOTHING is requested —
  // the ingest surface proves the refusal precedes the token exchange (there is no cache
  // entry, so a token fetch would have to hit the network first).
  it('refuses_a_file_outside_the_allowed_directories_before_any_request', async () => {
    const outside = await mkdtemp(join(tmpdir(), 'evidgate-attach-outside-'));
    directories.push(outside);
    const path = join(outside, 'trace.zip');
    await writeFile(path, 'trace-bytes');
    const store = harness(() => json(200, {}));
    const ingest = uploader(store, {
      env: env({
        EVIDGATE_ENV: 'ci',
        EVIDGATE_DEV_TOKEN: undefined,
        EVIDGATE_INGEST_TOKEN: ingestToken,
      }),
    });

    const error = await rejection(ingest.upload(input(path)));

    expect(error).toBeInstanceOf(CliError);
    expect(error.code).toBe('upload_path_refused');
    expect(store.calls).toHaveLength(0);
    // The filtered reason: the file is named, the absolute temp path is not.
    expect(error.message).toContain('trace.zip');
    expect(error.message).not.toContain(outside);
  });

  // AC6 boundary pair: exactly at the cap passes the guard, one byte over is refused. Both
  // files are sparse, so neither case reads or writes ten megabytes of data.
  it('accepts_a_file_exactly_at_its_kind_cap', async () => {
    const cap = capOf('screenshot');
    const path = join(root, 'shot.png');
    await sparseFile(path, cap);
    const digest = await digestOfFile(path);
    const store = acceptingStore(digest, { keepBody: false });

    const result = await uploader(store).upload(
      input(path, { kind: 'screenshot', mediaType: 'image/png' }),
    );

    expect(result).toEqual({ artifactId, bytes: cap });
    expect(store.calls.map((call) => call.method)).toEqual(['POST', 'POST', 'PUT', 'POST']);
  });

  it('refuses_a_file_one_byte_over_its_kind_cap_before_any_request', async () => {
    const cap = capOf('screenshot');
    const path = join(root, 'shot.png');
    await sparseFile(path, cap + 1);
    const store = harness(() => json(200, {}));

    const error = await rejection(
      uploader(store).upload(input(path, { kind: 'screenshot', mediaType: 'image/png' })),
    );

    expect(error.code).toBe('upload_too_large');
    expect(store.calls).toHaveLength(0);
  });
});

// `evidgate artifacts upload` on the binary — the successful run over the `uploadFile()`
// library (contracts/cli.md §12): the request the command sends, its two output shapes, the
// media type and attempt it derives, and the upload journal it writes. The flag and file
// guard refusals live in `artifactsUpload.validation.test.ts`, the server's own refusals in
// `artifactsUpload.refusals.test.ts`; the fixtures all three share are in
// `artifactsUpload.testing.ts`.
import { createHash } from 'node:crypto';
import { mkdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { journalFileName, type UploadIdentity } from '../uploadJournal.js';

import {
  artifactId,
  capture,
  executionKey,
  jsonResponse,
  runId,
  stubFetch,
  useArtifactsUploadSpec,
} from './artifactsUpload.testing.js';
import { devToken, mintedToken } from './devExchange.testing.js';

describe('artifacts upload', () => {
  const spec = useArtifactsUploadSpec();
  const { evidence, devEnv, happyStore, argv } = spec;
  let root: string;

  beforeEach(() => {
    root = spec.rootDir();
  });

  // AC1 / §5: the command prints the artifact id and its state, and sends the body the
  // openapi route documents (producer slot `shard-1` when nothing is given).
  it('uploads_and_prints_the_artifact', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stdout, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(0);

    expect(stdout.join('')).toBe(`artifact_id=${artifactId} state=ready bytes=11 file=trace.zip\n`);
    expect(stderr.join('')).toBe('');
    // The dev credential is an ingest token: one exchange at `/v1/ci/token` precedes the
    // upload, it presents the dev token, and the artifact calls bear the minted token.
    expect(store.exchanges.map((call) => call.url)).toStrictEqual([
      'http://127.0.0.1:8080/v1/ci/token',
    ]);
    expect(store.exchanges[0]?.json).toEqual({ ingest_token: devToken });
    const uploadUrl = store.calls[0];
    expect(uploadUrl?.url).toBe('http://127.0.0.1:8080/v1/artifacts/upload-url');
    expect(uploadUrl?.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(uploadUrl?.json).toEqual({
      run_id: runId,
      producer_id: 'shard-1',
      execution_key: executionKey,
      attempt_no: 1,
      kind: 'trace',
      media_type: 'application/zip',
      bytes: 11,
      sha256: createHash('sha256').update('trace-bytes').digest('hex').replace(/^/, 'sha256:'),
    });
    // The presigned URL never reaches stdout/stderr.
    expect(stdout.join('')).not.toContain('X-Amz');
  });

  it('prints_one_json_line_with_json', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stdout } = capture();

    await expect(
      main(argv(file, ['--json']), devEnv(), io, { fetch: store.fetchImpl }),
    ).resolves.toBe(0);

    expect(stdout.join('')).toBe(
      `${JSON.stringify({ file: 'trace.zip', artifact_id: artifactId, state: 'ready', bytes: 11, scan_state: 'pending' })}\n`,
    );
  });

  it('sends_the_given_media_type_and_attempt', async () => {
    const file = await evidence('snapshot.html', '<html></html>');
    const store = stubFetch((call) =>
      call.method === 'PUT'
        ? new Response(null, { status: 200 })
        : call.url.endsWith('/complete')
          ? jsonResponse(200, {
              artifact_id: artifactId,
              state: 'ready',
              bytes: 13,
              scan_state: 'pending',
              failure_code: null,
            })
          : jsonResponse(201, {
              artifact_id: artifactId,
              object_key: 'pending/org/x',
              upload_url: 'http://127.0.0.1:9/pending/x?X-Amz-Signature=abc',
              required_headers: {
                'Content-Length': '13',
                'x-amz-checksum-sha256': createHash('sha256')
                  .update('<html></html>')
                  .digest('base64'),
              },
              expires_at: '2026-10-24T23:59:59.000Z',
            }),
    );
    const { io } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'dom_snapshot',
          '--media-type',
          'text/html',
          '--attempt',
          '3',
          '--producer',
          'shard-7',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(0);

    expect(store.calls[0]?.json).toMatchObject({
      media_type: 'text/html',
      attempt_no: 3,
      producer_id: 'shard-7',
      kind: 'dom_snapshot',
    });
  });

  // §11.1 / §12: with EVIDGATE_SPOOL_DIR set the journal appears under the producer dir;
  // unset, the upload still works and no spool is created anywhere.
  it('writes_the_journal_when_a_spool_dir_is_set', async () => {
    const file = await evidence();
    const spoolDir = join(root, 'spool');
    await mkdir(spoolDir, { recursive: true });
    const store = stubFetch(happyStore());
    const { io } = capture();

    await expect(
      main(argv(file), devEnv({ EVIDGATE_SPOOL_DIR: spoolDir }), io, { fetch: store.fetchImpl }),
    ).resolves.toBe(0);

    // The record is named by the upload identity (contracts/cli.md §12.5), not by the id.
    const identity: UploadIdentity = {
      file_sha256: `sha256:${createHash('sha256').update('trace-bytes').digest('hex')}`,
      size: 11,
      kind: 'trace',
      execution_key: executionKey,
      attempt_no: 1,
      media_type: 'application/zip',
    };
    const journal = join(
      spoolDir,
      runId,
      'shard-1',
      'uploads',
      journalFileName(identity, artifactId),
    );
    const record = JSON.parse(await readFile(journal, 'utf8')) as Record<string, unknown>;
    expect(record).toMatchObject({ artifact_id: artifactId, put_done: true, complete_done: true });
  });

  it('uploads_without_a_spool_dir', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stdout } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(0);
    expect(stdout.join('')).toContain(`artifact_id=${artifactId}`);
  });
});

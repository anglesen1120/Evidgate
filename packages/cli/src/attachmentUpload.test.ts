// The drain signal and the descriptor lifetime of `createAttachmentUploader()`: an abort that
// lands after the digest mints nothing and puts nothing, and the descriptor the guard pinned is
// closed on the failing path as well as the succeeding one. The descriptor view (`/dev/fd` +
// `fstat`) does not exist on win32, which is why those cases are skipped there.
//
// Every HTTP call is answered in-process through the fake `fetch` seam; the only real I/O is a
// temp directory holding the attachment files. Bearer values are assembled at runtime, so no
// token-shaped literal is stored in the spec.
import { describe, expect, it } from 'vitest';

import {
  acceptingStore,
  afterExchange,
  cachedBearer,
  digestOfFile,
  harness,
  ingestToken,
  input,
  json,
  openDescriptors,
  rejection,
  runId,
  useAttachmentUploadSpec,
} from './attachmentUpload.testing.js';
import { exchangeBody } from './commands/devExchange.testing.js';
import { CliError } from './errors.js';
import { UploadFailedError } from './upload.js';

describe('createAttachmentUploader', () => {
  const { attachment, env, uploader } = useAttachmentUploadSpec();

  // Same deadline, one checkpoint later: the file was opened and hashed, and the abort lands
  // before the mint. The digest is done, so nothing may be requested for an artifact — no
  // upload-url, no PUT, no complete — and the pinned descriptor is still released.
  it.skipIf(process.platform === 'win32')(
    'mints_nothing_and_puts_nothing_when_the_signal_aborts_after_the_digest',
    async () => {
      const { path } = await attachment('trace-aborted.zip');
      const controller = new AbortController();
      const ciEnv = env({
        EVIDGATE_ENV: 'ci',
        EVIDGATE_DEV_TOKEN: undefined,
        EVIDGATE_INGEST_TOKEN: ingestToken,
      });
      // The CI exchange is the first request of an upload, so aborting inside it is the drain
      // deadline firing after the digest and before any artifact exists.
      const store = harness(() => {
        controller.abort();
        return json(
          200,
          exchangeBody(cachedBearer, {
            projectId: runId,
            expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
          }),
        );
      });

      const error = await rejection(
        uploader(store, { env: ciEnv }).upload(input(path), { signal: controller.signal }),
      );

      expect(error).toBeInstanceOf(CliError);
      expect(error.code).toBe('upload_aborted');
      expect(store.calls.map((call) => call.method)).toEqual(['POST']);
      expect(store.calls.some((call) => call.url.endsWith('/v1/artifacts/upload-url'))).toBe(false);
      expect(await openDescriptors(path)).toBe(0);
    },
  );

  // AC5 failure path: a 422 (unplanned attempt) is deterministic — the exchange, one
  // upload-url, no PUT and no complete — and the descriptor the guard pinned is released.
  it.skipIf(process.platform === 'win32')(
    'closes_the_file_descriptor_when_the_upload_is_refused',
    async () => {
      const { path } = await attachment('trace.zip');
      const store = harness(
        afterExchange(() =>
          json(422, {
            error: {
              code: 'unplanned_attempt',
              message: 'this attempt is not planned',
              request_id: 'req-9',
              retryable: false,
            },
          }),
        ),
      );
      expect(await openDescriptors(path)).toBe(0);

      const error = await rejection(uploader(store).upload(input(path)));

      expect(error).toBeInstanceOf(UploadFailedError);
      expect((error as UploadFailedError).status).toBe(422);
      expect((error as UploadFailedError).code).toBe('unplanned_attempt');
      expect(store.calls.map((call) => call.method)).toEqual(['POST', 'POST']);
      expect(await openDescriptors(path)).toBe(0);
    },
  );

  // The success path must leave nothing open either: a shard uploads many attachments in
  // one process, so a leaked descriptor per file would exhaust the limit mid-run.
  it.skipIf(process.platform === 'win32')(
    'closes_the_file_descriptor_when_the_upload_succeeds',
    async () => {
      const { path } = await attachment('trace.zip');
      const store = acceptingStore(await digestOfFile(path));

      await uploader(store).upload(input(path));

      expect(await openDescriptors(path)).toBe(0);
    },
  );
});

// The `complete` step: a committed `rejected` artifact and the reason it reports, its replay
// over a stored journal row, the server's own error code and request id, and the byte count
// the store has to prove before the upload counts as done.
import { describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import { UploadFailedError, uploadFile } from './upload.js';
import { journalFileName } from './uploadJournal.js';
import {
  artifactId,
  harness,
  json,
  readyBody,
  uploadUrlBody,
  useUploadSpec,
} from './upload.testing.js';

describe('uploadFile', () => {
  const { file, journal, journalIdentity, request } = useUploadSpec();

  // AC4 / §9 `complete_rejected`: a committed `rejected` is exit 3 naming the artifact and
  // the server's reason, with no retry.
  it('fails_on_a_rejected_complete', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: 'checksum_mismatch',
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('artifact_rejected');
    expect(error.message).toContain(artifactId);
    expect(error.message).toContain('checksum_mismatch');
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
  });

  // N7 (P3): `failure_code` is server text that lands in the message, and the message reaches
  // stderr and every `--json` consumer. Only a plain token is echoed verbatim; anything that
  // could repaint a terminal or forge a line is replaced by a stable marker.
  it.each([
    ['an escape sequence', 'checksum\u001b[31m_mismatch'],
    ['a newline', 'checksum\nmismatch'],
    ['an over-long value', 'a'.repeat(65)],
    ['a colon', 'checksum:mismatch'],
  ])('does_not_echo_a_failure_code_that_carries_%s', async (_case, failureCode) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: failureCode,
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as UploadFailedError;
    expect(error.code).toBe('artifact_rejected');
    expect(error.message).toContain('invalid_failure_code');
    expect(error.message).not.toContain(failureCode);
    expect(error.message).not.toContain('\u001b');
    expect(error.message).not.toContain('\n');
  });

  // The fallback is for unsafe text only: a `rejected` complete with no code at all is
  // `unknown`, which is what actually happened.
  it('names_a_rejection_without_a_failure_code_as_unknown', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: null,
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as UploadFailedError;
    expect(error.code).toBe('artifact_rejected');
    expect(error.message).toContain('rejected at complete: unknown');
    expect(error.message).not.toContain('invalid_failure_code');
  });

  // §12 replay: a rejection leaves `complete_done` false, so a re-run replays `complete`
  // against the same row and is rejected again — the hint names the journal row that must
  // be deleted to start over, because re-running alone never mints a second row.
  it('replays_a_rejected_complete_and_stays_terminal', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const rejection = (): Response =>
      json(200, {
        artifact_id: artifactId,
        state: 'rejected',
        bytes: null,
        scan_state: null,
        failure_code: 'checksum_mismatch',
      });

    const first = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return rejection();
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });
    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, first, resume)),
    ).rejects.toMatchObject({ code: 'artifact_rejected' });
    expect(await resume.findResumable(journalIdentity(sha256, 11))).toMatchObject({
      artifact_id: artifactId,
      put_done: true,
      complete_done: false,
    });

    // The restart has no upload-url and no PUT left to do: it can only ask `complete`.
    const replay = harness((call) =>
      call.url.endsWith('/complete') ? rejection() : json(500, { error: { code: 'unexpected' } }),
    );
    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, replay, resume));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('artifact_rejected');
    expect(replay.calls).toHaveLength(1);
    expect(replay.calls[0]?.url.endsWith(`/v1/artifacts/${artifactId}/complete`)).toBe(true);
    expect(error.hint).toContain('this row stays rejected');
    expect(error.hint).toContain(
      `uploads/${journalFileName(journalIdentity(sha256, 11), artifactId)}`,
    );
    expect((await resume.findResumable(journalIdentity(sha256, 11)))?.complete_done).toBe(false);
  });

  // §5 error fidelity: the server's code and request id survive; the status is kept so the
  // command can purge a dead token; deterministic statuses are never retried.
  it.each([409, 422])('surfaces_a_%i_complete_verbatim', async (status) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(status, {
          error: {
            code: status === 409 ? 'artifact_state_conflict' : 'artifact_invalid',
            message: 'the artifact cannot be completed',
            request_id: 'req-42',
            retryable: false,
          },
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as UploadFailedError;
    expect(error.status).toBe(status);
    expect(error.code).toBe(status === 409 ? 'artifact_state_conflict' : 'artifact_invalid');
    expect(error.requestId).toBe('req-42');
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
  });

  it('verifies_the_byte_count_complete_reports', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(12));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'request_failed' });
  });

  // F3 (P2): `ready` must prove the stored object is the file that was signed. openapi
  // lets `bytes` be null, so a null (or missing) count is a response with no evidence
  // behind it — never defaulted to the local size.
  it.each([
    ['null', { artifact_id: artifactId, state: 'ready', bytes: null, scan_state: 'pending' }],
    ['missing', { artifact_id: artifactId, state: 'ready', scan_state: 'pending' }],
  ])('refuses_a_ready_complete_whose_bytes_is_%s', async (_case, complete) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, complete);
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(UploadFailedError);
    expect((thrown as UploadFailedError).code).toBe('request_failed');
    expect((thrown as Error).message).toContain(artifactId);
  });
});

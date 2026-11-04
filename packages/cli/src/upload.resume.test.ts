// Journal-aware restarts: a stored upload answers from the journal instead of the store, a
// resumed row replays only the request it still owes, and the journal records no URL, bearer
// or header value.
import { mkdtemp, readdir, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import { SpoolLockedError } from './spoolLock.js';
import {
  deriveCompleteIdempotencyKey,
  deriveUploadUrlIdempotencyKey,
  uploadFile,
} from './upload.js';
import { UploadJournal, journalFileName } from './uploadJournal.js';
import {
  artifactId,
  executionKey,
  harness,
  json,
  producerId,
  readyBody,
  runId,
  secondArtifactId,
  token,
  uploadUrlBody,
  uploadUrlCall,
  useUploadSpec,
} from './upload.testing.js';

describe('uploadFile', () => {
  const { dirs, file, journal, journalIdentity, request } = useUploadSpec();

  // §9 `restart after put_done calls only complete` + the journal contract (§12).
  it('resumes_after_put_done_with_only_complete', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const failing = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(503, {
          error: { code: 'unavailable', message: 'down', request_id: 'r', retryable: true },
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, failing, resume)),
    ).rejects.toMatchObject({ code: 'unavailable' });
    const stored = await resume.findResumable(journalIdentity(sha256, 11));
    expect(stored).toMatchObject({ artifact_id: artifactId, put_done: true, complete_done: false });

    const restarted = harness((call) =>
      call.url.endsWith('/complete') ? json(200, readyBody(11)) : json(500, {}),
    );
    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, restarted, resume));

    expect(outcome.artifactId).toBe(artifactId);
    expect(restarted.calls).toHaveLength(1);
    expect(restarted.calls[0]?.url.endsWith(`/v1/artifacts/${artifactId}/complete`)).toBe(true);
    expect(restarted.calls[0]?.headers.get('idempotency-key')).toBe(
      deriveCompleteIdempotencyKey({ artifactId, fileSha256: sha256, size: 11 }),
    );
    expect((await resume.findResumable(journalIdentity(sha256, 11)))?.complete_done).toBe(true);
  });

  // §9 / §12: a second run over a completed journal row answers from the disk — no request,
  // so at most one artifact row per upload.
  it('does_not_upload_again_once_the_journal_is_complete', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const first = await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));
    const before = store.calls.length;
    const second = await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));

    expect(second.artifactId).toBe(first.artifactId);
    expect(second.state).toBe('ready');
    expect(store.calls.length).toBe(before);
  });

  // F5: `events send` holds spool.lock for the whole send, so a stored upload must not turn
  // into a failure when the journal cannot record it: `complete` is idempotent and the next
  // run replays it.
  it('does_not_fail_a_stored_upload_when_the_journal_lock_stays_busy', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-upload-busy-'));
    dirs.push(directory);
    const clock = { now: 1_000 };
    let locks = 0;
    // The first write (the row itself) gets the lock; every later one — put_done and
    // complete_done — is refused for longer than the bounded wait.
    const busy = await UploadJournal.open({
      producerDirectory: directory,
      withLock: <T>(fn: () => Promise<T>): Promise<T> => {
        locks += 1;
        if (locks === 1) return fn();
        return Promise.reject(new SpoolLockedError(join(directory, 'spool.lock'), 4_242));
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 1_800_000,
    });
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, store, busy));

    expect(outcome.state).toBe('ready');
    expect(outcome.bytes).toBe(11);
    expect(locks).toBeGreaterThan(1);
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
    // N1: the wait does not run to the 30 min deadline — the two best-effort updates stop at
    // the short default budget, so a busy `events send` costs seconds, not the whole upload.
    expect(clock.now - 1_000).toBeLessThan(10_000);
  });

  // N1 (P2): the best-effort budget is injectable, and it is per call: each of the two
  // post-transfer updates gives up inside it, so `complete` still runs with the deadline
  // almost untouched. Before the fix the wait ran until `deadlineAt`.
  it('gives_the_best_effort_journal_updates_a_short_lock_budget', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-upload-budget-'));
    dirs.push(directory);
    const clock = { now: 1_000 };
    let locks = 0;
    const busy = await UploadJournal.open({
      producerDirectory: directory,
      withLock: <T>(fn: () => Promise<T>): Promise<T> => {
        locks += 1;
        if (locks === 1) return fn();
        return Promise.reject(new SpoolLockedError(join(directory, 'spool.lock'), 4_242));
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 1_800_000,
    });
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const outcome = await uploadFile(
      request(
        { opened: fileHandle, sha256 },
        { deps: { ...store.deps, journalBestEffortWaitMs: 250 } },
        busy,
      ),
    );

    expect(outcome.state).toBe('ready');
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
    // put_done and complete_done each stop inside the injected 250 ms budget.
    expect(clock.now - 1_000).toBeGreaterThan(0);
    expect(clock.now - 1_000).toBeLessThan(500);
  });

  it('resumes_an_abandoned_mint_with_the_same_key', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    // First run: the mint lands in the journal, the PUT then dies with a permanent 500 on
    // every attempt (the deadline ends it) — the journal row is not put_done.
    const failing = harness((call) => {
      if (call.method === 'PUT') return new Response('nope', { status: 503 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });
    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, failing, resume)),
    ).rejects.toBeInstanceOf(CliError);
    const written = await resume.findResumable(journalIdentity(sha256, 11));
    expect(written).toMatchObject({ mint_no: 0, put_done: false, complete_done: false });

    const restarted = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });
    await uploadFile(request({ opened: fileHandle, sha256 }, restarted, resume));

    // The restart re-requests with mint 0 — the key the first run used — so the server
    // replays the stored row instead of minting a second artifact.
    expect(uploadUrlCall(restarted.calls).headers.get('idempotency-key')).toBe(
      deriveUploadUrlIdempotencyKey({
        runId,
        producerId,
        executionKey,
        attemptNo: 1,
        kind: 'trace',
        fileSha256: sha256,
        mintNo: 0,
      }),
    );
    expect(uploadUrlCall(restarted.calls).headers.get('idempotency-key')).toBe(
      uploadUrlCall(failing.calls).headers.get('idempotency-key'),
    );
  });

  // §8/§12 secret hygiene: the journal survives the process, so it must name no URL, no
  // bearer and no header value.
  it('never_writes_a_url_bearer_or_header_to_the_journal', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));

    const directory = resume.uploadsDirectory;
    const names = await readdir(directory);
    expect(names).toEqual([journalFileName(journalIdentity(sha256, 11), artifactId)]);
    const text = await readFile(join(directory, names[0] as string), 'utf8');
    expect(text).not.toContain('http');
    expect(text).not.toContain('X-Amz');
    expect(text).not.toContain('Signature');
    expect(text).not.toContain(token);
    const record = JSON.parse(text) as Record<string, unknown>;
    expect(record).toEqual({
      artifact_id: artifactId,
      file_sha256: sha256,
      size: 11,
      kind: 'trace',
      execution_key: executionKey,
      attempt_no: 1,
      media_type: 'application/zip',
      put_done: true,
      complete_done: true,
      mint_no: 0,
    });
  });

  // F1 (P1): the journal is keyed by the whole upload identity, not by (digest, size). The
  // same bytes under another execution, kind or attempt are a different server row, so the
  // command must call `upload-url` instead of replaying the other row's journal record.
  it.each([
    ['execution_key', { executionKey: 'b4'.repeat(32) }],
    ['attempt_no', { attemptNo: 2 }],
    ['kind', { kind: 'raw_report' as const, mediaType: 'application/xml' }],
    ['media_type', { mediaType: 'application/octet-stream' }],
  ])('starts_a_new_upload_for_another_%s', async (_field, overrides) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    // A real server mints a different artifact row for a different identity (the key
    // differs), so the responder does too.
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        const completed = call.url.slice(
          call.url.lastIndexOf('/artifacts/') + '/artifacts/'.length,
          -'/complete'.length,
        );
        return json(200, readyBody(11, completed));
      }
      const nth = store.calls.filter((entry) => entry.url.endsWith('/upload-url')).length;
      return json(
        201,
        uploadUrlBody({
          bytes: Buffer.from('trace-bytes'),
          sha256,
          artifact: nth === 1 ? artifactId : secondArtifactId,
        }),
      );
    });

    // The first run commits a `complete_done` row for this file (no request on a re-run).
    await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));
    const before = store.calls.length;
    const other = request({ opened: fileHandle, sha256 }, store, resume, overrides);
    await uploadFile(other);

    const urls = store.calls
      .slice(before)
      .filter((call) => call.url.endsWith('/artifacts/upload-url'));
    expect(urls).toHaveLength(1);
    expect(urls[0]?.headers.get('idempotency-key')).toBe(
      deriveUploadUrlIdempotencyKey({
        runId,
        producerId,
        executionKey: other.executionKey,
        attemptNo: other.attemptNo,
        kind: other.kind,
        fileSha256: sha256,
        mintNo: 0,
      }),
    );
  });
});

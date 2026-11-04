// What the store's answer may not do: sign a credential, framing or transport-owned header,
// move the PUT onto another path (a replaced file or a swapped symlink), or name an artifact
// that is not a UUID.
import { readdir, rename, rm, symlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { UploadFailedError, uploadFile } from './upload.js';
import { harness, json, readyBody, uploadUrlBody, useUploadSpec } from './upload.testing.js';

describe('uploadFile', () => {
  let root = '';
  const { file, journal, request } = useUploadSpec((directory) => {
    root = directory;
  });

  // F4 (P2, TOCTOU): the PUT streams a descriptor re-opened through the guard, so a file
  // replaced (or symlinked over) between the hash pass and the PUT is refused before a byte
  // leaves — never uploaded from the new path, never completed, never retried.
  it('refuses_a_file_replaced_between_the_passes', async () => {
    const { opened: fileHandle, sha256, path } = await file('trace.zip', 'first-content');
    const replacement = join(root, 'replacement.zip');
    await writeFile(replacement, 'first-content');
    await rename(replacement, path);
    const store = harness((call) =>
      call.method === 'PUT'
        ? new Response(null, { status: 200 })
        : json(201, uploadUrlBody({ bytes: Buffer.from('first-content'), sha256 })),
    );

    // The timer seam arms nothing real: the refusal happens before either PUT timer exists,
    // and a deadline timer armed before the re-open would outlive the refusal and keep the
    // CLI process alive for the rest of the upload budget.
    const armed: number[] = [];
    const cleared: unknown[] = [];
    const timers = {
      set: (milliseconds: number): unknown => {
        armed.push(milliseconds);
        return armed.length;
      },
      clear: (handle: unknown): void => {
        cleared.push(handle);
      },
    };

    await expect(
      uploadFile(
        request({ opened: fileHandle, sha256 }, { deps: { ...store.deps, timers } }, undefined),
      ),
    ).rejects.toMatchObject({ code: 'upload_path_refused' });
    expect(store.calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
    expect(store.calls.some((call) => call.url.endsWith('/complete'))).toBe(false);
    expect(store.sleeps).toEqual([]);
    expect(armed).toEqual([]);
    expect(cleared).toEqual([]);
  });

  it('refuses_a_symlink_swapped_in_between_the_passes', async () => {
    const { opened: fileHandle, sha256, path } = await file('trace.zip', 'first-content');
    const elsewhere = join(root, 'elsewhere.zip');
    await writeFile(elsewhere, 'first-content');
    await rm(path);
    await symlink(elsewhere, path);
    const store = harness((call) =>
      call.method === 'PUT'
        ? new Response(null, { status: 200 })
        : json(201, uploadUrlBody({ bytes: Buffer.from('first-content'), sha256 })),
    );

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'upload_path_refused' });
    expect(store.calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
    expect(store.sleeps).toEqual([]);
  });

  // F7 (P3): a 201 must not be able to make the CLI send a credential, retarget the host or
  // add a second body framing. The refusal names the header, never its value.
  it.each([
    'authorization',
    'Authorization',
    'cookie',
    'set-cookie',
    'host',
    'proxy-authorization',
    'transfer-encoding',
    'connection',
    // N5: the transport-owned set. `fetch` throws on most of these, and a thrown TypeError
    // reads as a transport failure — it would be retried until the deadline.
    'expect',
    'te',
    'trailer',
    'upgrade',
    'keep-alive',
    'content-encoding',
  ])('refuses_a_signed_%s_header', async (name) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed[name] = 'MARKER-VALUE-7';
    const store = harness(() => json(201, body));

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    const error = thrown as UploadFailedError;
    expect(error.code).toBe('request_failed');
    expect(error.message).toContain(name.toLowerCase());
    expect(error.message).not.toContain('MARKER-VALUE-7');
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // N5 (P3): a signed name `fetch` would refuse (a space, a colon, a non-token byte) is a
  // malformed response, not a transport failure. The name is not a token, so it is never
  // echoed — the message names the rule, not the attacker's text.
  it('refuses_a_signed_header_whose_name_is_not_a_token', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-bad name'] = 'MARKER-VALUE-7';
    const store = harness(() => json(201, body));

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    const error = thrown as UploadFailedError;
    expect(error.code).toBe('request_failed');
    expect(error.message).toContain('not an HTTP token');
    expect(error.message).not.toContain('x-amz-bad name');
    expect(error.message).not.toContain('MARKER-VALUE-7');
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // N5: a value `fetch` itself would refuse is refused here first, so it stays a
  // deterministic `request_failed` instead of a TypeError the retry loop repeats: CR/LF (a
  // smuggled second field), NUL, ESC (a terminal escape), DEL and a code point above Latin-1.
  it.each([
    ['a CRLF', 'ok\r\nX-Injected: 1'],
    ['a NUL', 'ok\u0000tail'],
    ['an ESC', 'ok\u001b[31mred'],
    ['a DEL', 'ok\u007ftail'],
    ['a non-Latin-1 code point', 'ok\u20ac'],
  ])('refuses_a_signed_header_value_with_%s', async (_case, value) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-meta-note'] = value;
    const store = harness(() => json(201, body));

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    const error = thrown as UploadFailedError;
    expect(error.code).toBe('request_failed');
    expect(error.message).toContain('x-amz-meta-note');
    expect(error.message).not.toContain('X-Injected');
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // The class is the one `fetch` accepts, not a blanket ASCII filter: tab and a Latin-1 byte
  // pass through untouched.
  it('accepts_a_signed_header_value_inside_the_fetch_class', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-meta-note'] = 'a\tb\u00e9';
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, body);
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    const put = store.calls.find((call) => call.method === 'PUT');
    expect(put?.headers.get('x-amz-meta-note')).toBe('a\tb\u00e9');
  });

  // F8 (P3): a malformed artifact id is a malformed response, not a corrupt journal: the
  // strict UUID check refuses it before anything is written.
  it('refuses_a_non_uuid_artifact_id_as_a_bad_response', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const store = harness(() =>
      json(
        201,
        uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256, artifact: '-'.repeat(36) }),
      ),
    );

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));
    } catch (error) {
      thrown = error;
    }
    expect((thrown as UploadFailedError).code).toBe('request_failed');
    expect(await readdir(resume.uploadsDirectory)).toEqual([]);
  });
});

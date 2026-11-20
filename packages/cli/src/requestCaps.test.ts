// One cap per request body: a body one byte over its cap is refused before the network sees
// it, a body exactly at the cap is sent, and the refusal text carries no body byte or token.
import {
  completeArtifact,
  createRun,
  createArtifactUploadUrl,
  exchangeCiToken,
  finalizeRun,
} from '@evidgate/sdk';
import { afterEach, describe, expect, it } from 'vitest';

import { callApi } from './callApi.js';
import { createApiClient } from './client.js';
import { CliError } from './errors.js';
import { resolveConfig } from './config.js';
import { resetSecrets } from './redact.js';
import {
  maxFinalizeBytes,
  maxManifestBytes,
  maxRunCreateBytes,
  maxTokenExchangeBytes,
  maxUploadUrlBytes,
  assertRequestBodyWithinCap,
  requestBodyCaps,
} from './requestCaps.js';
import { jsonResponse } from './commands/cliSpec.testing.js';

afterEach(() => {
  resetSecrets();
});

const config = resolveConfig({
  env: { EVIDGATE_API_URL: 'http://127.0.0.1:9', EVIDGATE_PROJECT: 'p' },
});
const uuid = '11111111-1111-4111-8111-111111111111';
// Assembled at runtime so no secret-shaped literal sits in the file.
const marker = ['egt', 'SECRETMARKER', 'value'].join('_');

function recorder(): {
  fetch: typeof fetch;
  calls: () => number;
  byteLengths: () => readonly number[];
} {
  let calls = 0;
  const byteLengths: number[] = [];
  return {
    calls: () => calls,
    byteLengths: () => byteLengths,
    fetch: async (input, init) => {
      calls += 1;
      const request = input instanceof Request ? input : new Request(input, init);
      byteLengths.push((await request.arrayBuffer()).byteLength);
      return jsonResponse(201, {});
    },
  };
}

/** A body of exactly `size` bytes that starts with the secret-shaped marker. */
function bodyOf(size: number): string {
  return marker.padEnd(size, 'x');
}

type Send = (
  client: ReturnType<typeof createApiClient>,
  body: string | Uint8Array,
) => Promise<unknown>;

const sends: Record<string, Send> = {
  'POST /runs/{r}/events': (client, body) =>
    client.post({
      url: '/runs/{r}/events',
      path: { r: uuid },
      body,
      bodySerializer: () => body,
    }),
  'POST /projects/{p}/manifests': (client, body) =>
    client.post({
      url: '/projects/{p}/manifests',
      path: { p: 'p' },
      headers: { 'Content-Type': 'application/json' },
      body,
      bodySerializer: null,
    }),
  'POST /projects/{p}/runs': (client, body) =>
    createRun({
      client,
      path: { p: 'p' },
      headers: { 'Idempotency-Key': 'k' },
      body: { manifest_id: uuid },
      bodySerializer: () => body,
    }),
  'POST /runs/{r}/finalize': (client, body) =>
    finalizeRun({
      client,
      path: { r: uuid },
      headers: { 'Idempotency-Key': 'k' },
      body: { producer_id: 'x', shard: 1, last_sequence: 1, count: 1, manifest_hash: 'h' },
      bodySerializer: () => body,
    }),
  'POST /ci/token': (client, body) =>
    exchangeCiToken({ client, body: {}, bodySerializer: () => body }),
  'POST /artifacts/upload-url': (client, body) =>
    createArtifactUploadUrl({
      client,
      headers: { 'Idempotency-Key': 'k' },
      body: {
        run_id: uuid,
        producer_id: 'x',
        execution_key: 'x',
        attempt_no: 1,
        kind: 'trace',
        media_type: 'application/zip',
        bytes: 1,
        sha256: 'sha256:x',
      },
      bodySerializer: () => body,
    }),
};

// The server limit each cap must stay at or under (route, limit): kept as data so the
// numbers are checked, not just quoted.
const serverLimits: Record<string, number> = {
  'POST /projects/{p}/manifests': 32 * 1024 * 1024,
  'POST /projects/{p}/runs': 64 * 1024,
  'POST /runs/{r}/finalize': 8 * 1024,
  'POST /ci/token': 4096,
  'POST /artifacts/upload-url': 8 * 1024,
  'POST /runs/{r}/events': 1_048_576,
};

describe('request body caps', () => {
  it('declares exactly the caps the server accepts, none above', () => {
    expect(Object.keys(requestBodyCaps).sort()).toEqual(Object.keys(serverLimits).sort());
    for (const [route, cap] of Object.entries(requestBodyCaps)) {
      expect(cap, route).toBeLessThanOrEqual(serverLimits[route]!);
    }
    expect([
      maxManifestBytes,
      maxRunCreateBytes,
      maxFinalizeBytes,
      maxTokenExchangeBytes,
      maxUploadUrlBytes,
    ]).toEqual([32 * 1024 * 1024, 65536, 8192, 4096, 8192]);
  });

  describe.each(Object.keys(sends))('%s', (route) => {
    const cap = requestBodyCaps[route]!;
    const send = sends[route]!;

    it('refuses one byte over the cap with zero requests, once, naming no body byte', async () => {
      const transport = recorder();
      const client = createApiClient(config, marker, transport.fetch);
      const oversize = route.includes('manifests')
        ? new TextEncoder().encode(bodyOf(cap + 1))
        : bodyOf(cap + 1);
      let attempts = 0;
      const outcome = await callApi(
        () => {
          attempts += 1;
          return send(client, oversize) as Promise<{ error: unknown }>;
        },
        { policy: 'replay-safe', deps: { sleep: () => Promise.resolve(), random: () => 0 } },
      ).then(
        () => undefined,
        (error: unknown) => error,
      );
      expect(outcome).toBeInstanceOf(CliError);
      expect((outcome as CliError).code).toBe('request_too_large');
      expect((outcome as CliError).hint).toBe('this request was not sent');
      expect((outcome as CliError).message).not.toContain(marker);
      expect((outcome as CliError).message).not.toContain('xxxx');
      expect(transport.calls()).toBe(0);
      expect(attempts).toBe(1);
    });

    it('sends a body exactly at the cap', async () => {
      const transport = recorder();
      const client = createApiClient(config, marker, transport.fetch);
      const exact = route.includes('manifests')
        ? new TextEncoder().encode(bodyOf(cap))
        : bodyOf(cap);
      await send(client, exact);
      expect(transport.calls()).toBe(1);
      expect(transport.byteLengths()).toEqual([cap]);
    });
  });

  it('keeps a real exchange body far under 4096 (the GitHub OIDC JWT travels as a bearer)', async () => {
    // egt_<12-char prefix>_<43-char base64url secret> plus a 128-char one-time challenge.
    const ingest = `egt_${'a'.repeat(12)}_${'b'.repeat(43)}`;
    const body = JSON.stringify({ ingest_token: ingest, binding_challenge: 'c'.repeat(128) });
    expect(Buffer.byteLength(body)).toBeLessThan(maxTokenExchangeBytes / 10);
    const transport = recorder();
    const client = createApiClient(config, marker, transport.fetch);
    await exchangeCiToken({
      client,
      body: { ingest_token: ingest, binding_challenge: 'c'.repeat(128) },
    });
    expect(transport.calls()).toBe(1);
  });

  it('refuses a body-carrying route that has no cap (fail closed)', async () => {
    const transport = recorder();
    const client = createApiClient(config, marker, transport.fetch);
    const outcome = await client
      .post({ url: '/unlisted', body: 'abc', bodySerializer: () => 'abc' })
      .then(
        () => undefined,
        (error: unknown) => error,
      );
    expect((outcome as CliError).code).toBe('request_failed');
    expect(transport.calls()).toBe(0);
  });

  it.each([
    { form: 'empty string', body: '' },
    { form: 'empty typed array', body: new Uint8Array(0) },
  ])('refuses a present $form on a route with no cap', async ({ body }) => {
    const transport = recorder();
    const client = createApiClient(config, marker, transport.fetch);
    await expect(
      client.post({ url: '/unlisted', body, bodySerializer: null }),
    ).rejects.toMatchObject({ code: 'request_failed' });
    expect(transport.calls()).toBe(0);
  });

  it.each([
    { form: 'empty string', body: '' },
    { form: 'empty typed array', body: new Uint8Array(0) },
  ])('sends a present $form on a capped route', async ({ body }) => {
    const transport = recorder();
    const client = createApiClient(config, marker, transport.fetch);
    await client.post({
      url: '/projects/{p}/manifests',
      path: { p: 'p' },
      body,
      bodySerializer: null,
    });
    expect(transport.calls()).toBe(1);
    expect(transport.byteLengths()).toEqual([0]);
  });

  it.each([
    { form: 'empty string', serializedBody: '' },
    { form: 'undefined', serializedBody: undefined },
  ])('omits a serializer output of $form on a route with no cap', async ({ serializedBody }) => {
    const transport = recorder();
    const client = createApiClient(config, marker, transport.fetch);
    await client.post({ url: '/unlisted', body: {}, bodySerializer: () => serializedBody });
    expect(transport.calls()).toBe(1);
    expect(transport.byteLengths()).toEqual([0]);
  });

  it.each([undefined, null])('accepts an absent body (%s) on a route with no cap', (body) => {
    expect(() => assertRequestBodyWithinCap('POST', '/unlisted', body)).not.toThrow();
  });

  it('refuses a body whose length cannot be measured', () => {
    const route = 'POST /projects/{p}/manifests';
    for (const body of [new ReadableStream(), new ArrayBuffer(4), { a: 1 }]) {
      expect(
        () => assertRequestBodyWithinCap('POST', '/projects/{p}/manifests', body),
        route,
      ).toThrow(CliError);
    }
  });

  it('does not refuse a request that carries no body', async () => {
    const transport = recorder();
    const client = createApiClient(config, marker, transport.fetch);
    await client.get({ url: '/runs/{r}/decision', path: { r: uuid } });
    await completeArtifact({ client, path: { a: uuid }, headers: { 'Idempotency-Key': 'k' } });
    expect(transport.calls()).toBe(2);
    expect(transport.byteLengths()).toEqual([0, 0]);
  });
});

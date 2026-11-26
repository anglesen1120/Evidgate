import { createHmac } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { verifyWebhook } from './verify-webhook.js';

const now = 1800000000;
const secret = 'whsec_fixture';
const id = '018f0000-0000-7000-8000-000000000011';
const body = Buffer.from(`{ "evaluation_id": "${id}", "reason": "café" }\n`);
const sign = (bytes: Buffer) => createHmac('sha256', secret).update(bytes).digest('hex');

function delivery(timestamp = now) {
  const digest = sign(Buffer.concat([Buffer.from(`${timestamp}.`), body]));
  const headers: Record<string, string> = {
    'Evidgate-Event-Id': id,
    'Evidgate-Event-Type': 'evaluation.created',
    'Evidgate-Timestamp': String(timestamp),
    'Evidgate-Signature': `v1=${digest}`,
  };
  return { secret, headers, body, now, seenEventIds: new Set<string>() };
}

describe('receiver conformance', () => {
  it('acknowledges a fresh delivery and deduplicates its replay', () => {
    const d = delivery();
    expect(verifyWebhook(d)).toEqual({ status: 200, duplicate: false });
    expect(verifyWebhook(d)).toEqual({ status: 200, duplicate: true });
  });

  it.each([
    [-360, 401],
    [-301, 401],
    [-300, 200],
    [300, 200],
    [301, 401],
  ])('checks signed timestamp offset %i against the inclusive tolerance', (offset, status) =>
    expect(verifyWebhook(delivery(now + offset)).status).toBe(status),
  );

  it('rejects tampered raw bytes', () => {
    expect(verifyWebhook({ ...delivery(), body: body.subarray(0, -1) }).status).toBe(401);
  });

  it.each(['0'.repeat(64), sign(body), 'a'.repeat(63), 'a'.repeat(65), 'g'.repeat(64)])(
    'rejects forged, timestamp-free or malformed MAC %s without consuming the ID',
    (hex) => {
      const d = delivery();
      d.headers['Evidgate-Signature'] = `v1=${hex}`;
      expect(verifyWebhook(d).status).toBe(401);
      expect(d.seenEventIds.size).toBe(0);
    },
  );

  it('accepts a matching uppercase MAC among multiple signatures', () => {
    const d = delivery();
    const hex = d.headers['Evidgate-Signature']!.slice(3).toUpperCase();
    d.headers['Evidgate-Signature'] = `v1=${'0'.repeat(64)}, v1=${hex}`;
    expect(verifyWebhook(d).status).toBe(200);
  });

  it.each(Object.keys(delivery().headers))('rejects missing %s', (name) => {
    const d = delivery();
    delete d.headers[name];
    expect(verifyWebhook(d).status).toBe(401);
  });

  it.each([
    ['Evidgate-Event-Id', '018f0000-0000-7000-8000-000000000012'],
    ['Evidgate-Event-Type', 'other.event'],
    ['Evidgate-Timestamp', `+${now}`],
  ])('rejects invalid or substituted %s', (name, value) => {
    const d = delivery();
    d.headers[name] = value;
    expect(verifyWebhook(d).status).toBe(401);
  });

  it('rejects a correctly signed body that does not name the event id', () => {
    const d = delivery();
    const other = Buffer.from('not json');
    d.headers['Evidgate-Signature'] = `v1=${sign(Buffer.concat([Buffer.from(`${now}.`), other]))}`;
    expect(verifyWebhook({ ...d, body: other }).status).toBe(401);
  });
});

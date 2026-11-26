import { createHmac } from 'node:crypto';
import { once } from 'node:events';
import type { Server } from 'node:http';
import { connect, type AddressInfo } from 'node:net';
import { afterEach, describe, expect, it } from 'vitest';
import { createReceiver } from './server.js';

const now = 1800000000;
const secret = 'whsec_fixture';
const id = '018f0000-0000-7000-8000-000000000011';
const body = JSON.stringify({ evaluation_id: id });
const servers: Server[] = [];

async function start(seenEventIds = new Set<string>()): Promise<{ server: Server; port: number }> {
  const server = createReceiver({ secret, now: () => now, seenEventIds });
  servers.push(server);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return { server, port: (server.address() as AddressInfo).port };
}

function signedHeaders(): Record<string, string> {
  const mac = createHmac('sha256', secret).update(`${now}.${body}`).digest('hex');
  return {
    'Evidgate-Event-Id': id,
    'Evidgate-Event-Type': 'evaluation.created',
    'Evidgate-Timestamp': String(now),
    'Evidgate-Signature': `v1=${mac}`,
  };
}

async function send(
  port: number,
  payload: string,
  mutate: (h: Record<string, string>) => void = () => {},
) {
  const headers = signedHeaders();
  mutate(headers);
  const res = await fetch(`http://127.0.0.1:${port}/`, {
    method: 'POST',
    headers,
    body: payload,
    redirect: 'manual',
  });
  return { status: res.status, json: await res.json() };
}

afterEach(() => {
  for (const server of servers.splice(0)) server.close();
});

describe('receiver conformance (http)', () => {
  it('answers 200 to a signed delivery and 401 to a forged one', async () => {
    const { port } = await start();
    expect(await send(port, body)).toEqual({ status: 200, json: { duplicate: false } });
    const forged = await send(
      port,
      body,
      (h) => (h['Evidgate-Signature'] = `v1=${'0'.repeat(64)}`),
    );
    expect(forged).toEqual({ status: 401, json: { error: 'bad_signature' } });
  });

  it('answers 413 above the body cap', async () => {
    const { port } = await start();
    expect((await send(port, 'x'.repeat(1024 * 1024 + 1))).status).toBe(413);
  });

  it('survives a client that disconnects mid-body and still accepts the next delivery', async () => {
    const seen = new Set<string>();
    const { server, port } = await start(seen);
    const unhandled: unknown[] = [];
    const record = (reason: unknown) => unhandled.push(reason);
    process.on('unhandledRejection', record);

    // The aborted request closes after its body read has failed; one more turn lets a rejection
    // that nobody handled surface before it is checked.
    const requestClosed = once(server, 'request').then(([req]) =>
      once(req as NodeJS.EventEmitter, 'close').catch(() => undefined),
    );
    const client = connect(port, '127.0.0.1');
    await once(client, 'connect');
    const headers = Object.entries(signedHeaders()).map(([k, v]) => `${k}: ${v}\r\n`);
    client.write(
      `POST / HTTP/1.1\r\nHost: x\r\nContent-Length: ${body.length}\r\n${headers.join('')}\r\n${body.slice(0, 5)}`,
    );
    client.destroy();
    await requestClosed;
    await new Promise((resolve) => setImmediate(resolve));
    process.off('unhandledRejection', record);

    expect(unhandled).toEqual([]);
    expect(seen.size).toBe(0);
    expect(await send(port, body)).toEqual({ status: 200, json: { duplicate: false } });
  });
});

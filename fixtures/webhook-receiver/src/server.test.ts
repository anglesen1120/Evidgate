import { createHmac } from 'node:crypto';
import { once } from 'node:events';
import { request, type IncomingMessage, type Server } from 'node:http';
import { connect, type AddressInfo } from 'node:net';
import { text } from 'node:stream/consumers';
import { afterEach, describe, expect, it } from 'vitest';
import { createReceiver } from './server.js';

const now = 1800000000;
const secret = 'whsec_fixture';
const id = '018f0000-0000-7000-8000-000000000011';
const body = JSON.stringify({ evaluation_id: id });
const servers: Server[] = [];

async function start(
  eventHashes = new Map<string, string>(),
): Promise<{ server: Server; port: number }> {
  const server = createReceiver({ secret, now: () => now, eventHashes });
  servers.push(server);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return { server, port: (server.address() as AddressInfo).port };
}

function signedHeaders(payload = body): Record<string, string> {
  const mac = createHmac('sha256', secret).update(`${now}.${payload}`).digest('hex');
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
  const headers = signedHeaders(payload);
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
  it.each([undefined, ''])('refuses to start without a configured secret (%s)', (missing) => {
    expect(() => createReceiver({ secret: missing })).toThrow('WEBHOOK_SECRET');
  });

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

  it('answers 409 when a signed delivery reuses an id with different bytes', async () => {
    const { port } = await start();
    expect(await send(port, body)).toEqual({ status: 200, json: { duplicate: false } });
    expect(await send(port, body + '\n')).toEqual({
      status: 409,
      json: { error: 'event_payload_conflict' },
    });
    expect(await send(port, body)).toEqual({ status: 200, json: { duplicate: true } });
  });

  it('accepts a signed body exactly at the byte cap', async () => {
    const { port } = await start();
    expect(await send(port, body.padEnd(1024 * 1024))).toEqual({
      status: 200,
      json: { duplicate: false },
    });
  });

  it('sends a complete 413 before a chunked upload ends and closes the connection', async () => {
    const { port } = await start();
    const upload = request({ host: '127.0.0.1', port, method: 'POST' });
    // The workspace targets ES2022, which has no Promise.withResolvers.
    const responded = new Promise<IncomingMessage>((resolve, reject) => {
      upload.on('response', resolve);
      upload.on('error', reject);
    });
    upload.write(Buffer.alloc(1024 * 1024 + 1));
    const response = await responded;
    expect(response.statusCode).toBe(413);
    expect(response.headers.connection).toBe('close');
    expect(JSON.parse(await text(response))).toEqual({ error: 'body_too_large' });
    upload.destroy();
  });

  it('survives a client that disconnects mid-body and still accepts the next delivery', async () => {
    const seen = new Map<string, string>();
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

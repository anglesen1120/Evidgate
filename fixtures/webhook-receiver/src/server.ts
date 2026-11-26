import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { verifyWebhook } from './verify-webhook.js';

const MAX_BODY_BYTES = 1024 * 1024;

export interface ReceiverOptions {
  readonly secret: string;
  /** Unix seconds; injectable so a test does not depend on the wall clock. */
  readonly now?: () => number;
  readonly seenEventIds?: Set<string>;
}

async function readBody(req: IncomingMessage): Promise<Buffer | null> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    const buf = chunk as Buffer;
    size += buf.length;
    if (size > MAX_BODY_BYTES) return null;
    chunks.push(buf);
  }
  return Buffer.concat(chunks);
}

function reply(res: ServerResponse, status: number, payload: object): void {
  res.writeHead(status, { 'content-type': 'application/json' });
  res.end(JSON.stringify(payload));
}

/** A receiver that answers 2xx only for authenticated deliveries and never follows up with a redirect. */
export function createReceiver(options: ReceiverOptions): Server {
  const seenEventIds = options.seenEventIds ?? new Set<string>();
  const now = options.now ?? (() => Math.floor(Date.now() / 1000));
  return createServer((req, res) => {
    if (req.method !== 'POST') return reply(res, 405, { error: 'method_not_allowed' });
    // A client that drops mid-body rejects the read: nothing was verified or recorded, and the
    // socket is gone, so the only safe reaction is to close our side.
    readBody(req)
      .then((body) => {
        if (body === null) return reply(res, 413, { error: 'body_too_large' });
        const result = verifyWebhook({
          secret: options.secret,
          headers: req.headers,
          body,
          now: now(),
          seenEventIds,
        });
        if (result.status === 401) return reply(res, 401, { error: result.reason });
        reply(res, 200, { duplicate: result.duplicate });
      })
      .catch(() => res.destroy());
  });
}

import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { verifyWebhook } from './verify-webhook.js';

const MAX_BODY_BYTES = 1024 * 1024;

export interface ReceiverOptions {
  /** Required: the receiver refuses to start without it (read it from WEBHOOK_SECRET). */
  readonly secret: string | undefined;
  /** Unix seconds; injectable so a test does not depend on the wall clock. */
  readonly now?: () => number;
  readonly eventHashes?: Map<string, string>;
}

/**
 * Resolves null once the cap is crossed. The request is drained and never destroyed, because
 * destroying the socket would discard the 413 before it is written.
 */
function readBody(req: IncomingMessage): Promise<Buffer | null> {
  // The workspace targets ES2022, which has no Promise.withResolvers.
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let settled = false;
    req.on('data', (chunk: Buffer) => {
      if (settled) return;
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        settled = true;
        chunks.length = 0;
        resolve(null);
      } else chunks.push(chunk);
    });
    req.on('end', () => {
      if (settled) return;
      settled = true;
      resolve(Buffer.concat(chunks));
    });
    req.on('error', reject);
    req.on('close', () => {
      if (settled) return;
      settled = true;
      reject(new Error('request closed before the body ended'));
    });
  });
}

function reply(
  res: ServerResponse,
  status: number,
  payload: object,
  extraHeaders: Record<string, string> = {},
): void {
  res.writeHead(status, { 'content-type': 'application/json', ...extraHeaders });
  res.end(JSON.stringify(payload));
}

/** A receiver that answers 2xx only for authenticated deliveries and never follows up with a redirect. */
export function createReceiver(options: ReceiverOptions): Server {
  const secret = options.secret;
  if (!secret) throw new Error('WEBHOOK_SECRET is required: refusing to start without it');
  const eventHashes = options.eventHashes ?? new Map<string, string>();
  const now = options.now ?? (() => Math.floor(Date.now() / 1000));
  return createServer((req, res) => {
    if (req.method !== 'POST') return reply(res, 405, { error: 'method_not_allowed' });
    // A client that drops mid-body rejects the read: nothing was verified or recorded, and the
    // socket is gone, so the only safe reaction is to close our side.
    readBody(req)
      .then((body) => {
        if (body === null) {
          return reply(res, 413, { error: 'body_too_large' }, { connection: 'close' });
        }
        const result = verifyWebhook({
          secret,
          headers: req.headers,
          body,
          now: now(),
          eventHashes,
        });
        if (result.status !== 200) return reply(res, result.status, { error: result.reason });
        reply(res, 200, { duplicate: result.duplicate });
      })
      .catch(() => res.destroy());
  });
}

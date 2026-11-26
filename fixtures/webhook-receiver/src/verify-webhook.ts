import { createHmac, timingSafeEqual } from 'node:crypto';

/** Deliveries signed further than this from the receiver's clock are refused (replay window). */
export const TOLERANCE_SECONDS = 300;

const EVENT_TYPE = 'evaluation.created';
const MAX_SIGNATURES = 8;
const HEX_MAC = /^[0-9a-fA-F]{64}$/;
const UNIX_SECONDS = /^[0-9]{1,12}$/;

export type HeaderValue = string | readonly string[] | undefined;

export interface VerifyInput {
  /** The endpoint secret exactly as shown at registration (the literal `whsec_...` text). */
  readonly secret: string;
  readonly headers: Readonly<Record<string, HeaderValue>>;
  /** The raw request bytes: the MAC covers them, so never re-serialise parsed JSON. */
  readonly body: Uint8Array | string;
  /** The receiver's clock in unix seconds. */
  readonly now: number;
  /** Event ids already processed; a real receiver keeps this in durable storage. */
  readonly seenEventIds: Set<string>;
}

export type VerifyResult =
  | { readonly status: 200; readonly duplicate: boolean }
  | { readonly status: 401; readonly reason: string };

const reject = (reason: string): VerifyResult => ({ status: 401, reason });

function header(headers: VerifyInput['headers'], name: string): string | undefined {
  const key = Object.keys(headers).find((k) => k.toLowerCase() === name.toLowerCase());
  const value = key === undefined ? undefined : headers[key];
  return typeof value === 'string' ? value : undefined;
}

/** Candidate MACs of the `v1=` entries; other versions and malformed entries are ignored. */
function candidateMacs(signatureHeader: string): Buffer[] {
  return signatureHeader
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part.startsWith('v1='))
    .slice(0, MAX_SIGNATURES)
    .map((part) => part.slice(3))
    .filter((hex) => HEX_MAC.test(hex))
    .map((hex) => Buffer.from(hex, 'hex'));
}

/** The event id is not signed on its own, so it must equal the signed body's evaluation_id. */
function signedEvaluationId(body: Buffer): unknown {
  try {
    const parsed: unknown = JSON.parse(body.toString('utf8'));
    return typeof parsed === 'object' && parsed !== null && 'evaluation_id' in parsed
      ? parsed.evaluation_id
      : undefined;
  } catch {
    return undefined;
  }
}

export function verifyWebhook(input: VerifyInput): VerifyResult {
  const eventId = header(input.headers, 'Evidgate-Event-Id');
  const eventType = header(input.headers, 'Evidgate-Event-Type');
  const timestamp = header(input.headers, 'Evidgate-Timestamp');
  const signature = header(input.headers, 'Evidgate-Signature');
  if (!eventId || !eventType || !timestamp || !signature) return reject('missing_header');
  if (!UNIX_SECONDS.test(timestamp)) return reject('malformed_timestamp');
  if (Math.abs(input.now - Number(timestamp)) > TOLERANCE_SECONDS) return reject('stale_timestamp');

  const body =
    typeof input.body === 'string' ? Buffer.from(input.body, 'utf8') : Buffer.from(input.body);
  const expected = createHmac('sha256', input.secret).update(`${timestamp}.`).update(body).digest();
  // Every candidate is compared so the work does not depend on which one matches.
  const matches = candidateMacs(signature).map((mac) => timingSafeEqual(mac, expected));
  if (!matches.includes(true)) return reject('bad_signature');

  if (eventType !== EVENT_TYPE) return reject('unsupported_event_type');
  if (signedEvaluationId(body) !== eventId) return reject('event_id_mismatch');

  // Only an authenticated id is recorded, so a forger cannot burn a real event's id.
  const duplicate = input.seenEventIds.has(eventId);
  input.seenEventIds.add(eventId);
  return { status: 200, duplicate };
}

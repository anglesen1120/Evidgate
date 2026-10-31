// Hand-written UUIDv7 (RFC 9562) for `event_id`, because no shared TypeScript helper exists
// (packages/cli uses a v4) and this package must stay dependency-light: its one runtime load
// of @evidgate/cli is a single lazy `import()` in `attach.ts`, and the dependency is one-way.
// The 48-bit big-endian millisecond timestamp keeps ids roughly sortable; the rest is random.
import { randomBytes } from 'node:crypto';

/** Milliseconds since the Unix epoch that fit in the 48-bit UUIDv7 timestamp field. */
const MAX_TIMESTAMP = 2 ** 48 - 1;

/**
 * Returns a fresh UUIDv7 string. `now` is injectable so a test can pin the timestamp.
 */
export function uuidv7(now: number = Date.now()): string {
  if (!Number.isInteger(now) || now < 0 || now > MAX_TIMESTAMP) {
    throw new RangeError(`uuidv7 timestamp out of range: ${String(now)}`);
  }
  const bytes = randomBytes(16);
  // 48-bit big-endian Unix time in milliseconds (bytes 0..5).
  bytes.writeUIntBE(now, 0, 6);
  // Version 7 in the high nibble of byte 6; RFC 9562 variant (10) in the high bits of byte 8.
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x70;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = bytes.toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

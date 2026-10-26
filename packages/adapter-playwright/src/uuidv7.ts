// 04.3.1-T1.1: hand-written UUIDv7 (RFC 9562) for `event_id` (AC4). The repo has no
// shared TypeScript helper — packages/cli uses node:crypto.randomUUID (a v4) — and the
// reporter is a standalone, dependency-light package (04.3.3-T2: the one runtime load of
// @evidgate/cli is the single lazy `import()` in `attach.ts`, and the dependency stays
// one-way — packages/cli never imports this adapter), so the 20 lines live here. The
// 48-bit big-endian millisecond timestamp keeps ids roughly sortable; the remaining 74
// bits are random.
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

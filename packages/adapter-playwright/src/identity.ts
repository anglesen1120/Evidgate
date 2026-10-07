// 04.3.1-T1.1: the execution_key is computed by the server from the manifest (00 §7);
// the reporter must not derive it. Until 04.3.2-T1 supplies the real identity mapping
// (annotation `evidgate.id` → case_id, project + parameters → variant_hash), the
// reporter emits this placeholder behind an injectable resolver. The value is a valid
// `^[0-9a-f]{64}$` per contracts/schemas/event-envelope.v1.json, so envelopes validate
// end to end while the identity mapping is still open.
import type { TestCase } from '@playwright/test/reporter';

/** 64 hex zeros — a schema-valid stand-in until 04.3.2-T1 lands. */
export const PLACEHOLDER_EXECUTION_KEY = '0'.repeat(64);

export interface Identity {
  execution_key: string;
}

/**
 * Maps a test case to its execution identity. 04.3.2-T1 replaces the default
 * (placeholder) resolver with the real manifest-derived mapping.
 */
export type IdentityResolver = (test: TestCase) => Identity;

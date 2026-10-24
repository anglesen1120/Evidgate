// 09.1.1-T3.2 (00 §9.1; ADR-0008 rule 2): an `Intent` is the caller's identity for one user
// action. Its `Idempotency-Key` is minted once, when the user presses the button, and reused on
// every retry of that action — a new key for the same action would let the server create the
// resource twice, and a shared key across two actions would turn the second into a 409
// `idempotency_conflict`.

import { newUuid } from './random-id';

/** One user intent: the key a mutation is retried with until the action is done or abandoned. */
export interface Intent {
  readonly idempotencyKey: string;
}

/**
 * Mint an intent for a user action. `newUuid()` yields a v4 UUID (122 random bits), which stays
 * unique per `(org_id, project_id, operation)` for the 24 h the server remembers a key, and
 * still works in a non-secure context where `crypto.randomUUID` is absent.
 */
export function createIntent(): Intent {
  return { idempotencyKey: newUuid() };
}

/**
 * The SDK `headers` fragment carrying the intent's key. The data service passes it in
 * `options.headers` because `HttpContext` is unreachable from an SDK call site (the adapter owns
 * the `HttpRequest`); the idempotency interceptor only fills a key that no caller supplied.
 */
export function intentHeaders(intent: Intent): { 'Idempotency-Key': string } {
  return { 'Idempotency-Key': intent.idempotencyKey };
}

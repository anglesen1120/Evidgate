// The shapes the two halves of `events send` share: the injected seams a spec replaces, and
// what one producer contributed to the report.
import type { ExchangeDeps } from '../auth/exchange.js';

/**
 * The seams `main` forwards (`ExchangeDeps`) plus the ones a deadline-bounded send needs
 * (`SendDeps`) and the stdin surrogate for `--from -`. All are optional, so the production
 * path falls back to the real clock, `Math.random`, the global `fetch`, a real timer and
 * `process.stdin`; a spec injects each of them, which is what makes an outage window and a
 * restart deterministic with no real waiting.
 */
export interface EventsSendSeams extends ExchangeDeps {
  readonly now?: () => number;
  readonly random?: () => number;
  readonly stdin?: AsyncIterable<Uint8Array>;
}

/** What one producer contributed to the report; only producers with work appear. */
export interface ProducerReport {
  readonly producerId: string;
  readonly batches: number;
  readonly accepted: number;
  readonly duplicates: number;
}

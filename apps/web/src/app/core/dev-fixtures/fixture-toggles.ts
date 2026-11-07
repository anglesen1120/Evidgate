// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12; 01.6.1-T3 fixture corpus): the runtime switches a
// UI review uses to reach the states a single fixture cannot show — a second variant of one
// operation (an empty page, a 403, a 412) and a slow answer for the loading state.
//
// A toggle is read per request, from memory, by the dev fixture backend only (`fixture-backend.ts`).
// It never becomes a request header, a query parameter or a cookie, so flipping a state can never
// change what the app believes about the server; the fixture corpus stays the only source of
// response bodies (a derived catalog row replaces one member of a corpus body, never the body).
// Initial values come from the page URL so a bookmarked review URL reopens in the same state:
//
//   ?fixture.variant.<operationId>=<variant>   e.g. ?fixture.variant.listRuns=forbidden
//   ?fixture.latency=<ms>                      every operation, unless a per-operation latency wins
//
// A latency is a whole, non-negative number of milliseconds, clamped to ten minutes, from the URL
// and from `setLatency` alike; anything else (`abc`, `-5`, `1e3`, `1.5`, empty) is ignored. `'*'` is
// a wildcard for latency only: the backend asks for the matched route's operation id alone, so a
// stored `'*'` variant (`fixture.variant.*=…`) is never consulted and changes no answer.
//
// An unknown variant or operation is answered by the backend as HTTP 501 `fixture_not_found`, which
// is the state a stale bookmark should show rather than a silently different fixture.
//
// Dev-only: nothing in the production provider graph imports this file (`dev-backend.ts` is the
// empty production stand-in and `dev-backend.fixtures.ts` the `fixtures` configuration's
// replacement), and `scripts/check-no-fixture-code.mjs` fails the production build if it leaks.

import { DOCUMENT } from '@angular/common';
import { inject, Injectable } from '@angular/core';

/** The variant a fixture corpus names as its default answer for an operation. */
export const DEFAULT_FIXTURE_VARIANT = 'ok';

/** The latency key that applies to every operation a per-operation latency does not cover. */
export const ALL_OPERATIONS = '*';

/** URL parameter prefix selecting one operation's variant: `fixture.variant.<operationId>`. */
export const VARIANT_PARAM_PREFIX = 'fixture.variant.';

/** URL parameter selecting a global latency in milliseconds. */
export const LATENCY_PARAM = 'fixture.latency';

/** Upper bound on a latency toggle: ten minutes, the point past which a review URL is stuck. */
const MAX_LATENCY_MS = 600_000;

/**
 * The one latency rule, shared by the `fixture.latency` parameter and `setLatency`: a whole,
 * non-negative number of milliseconds, clamped to the ten-minute cap so a too-large but well-formed
 * value still yields a delay instead of a stuck review. Anything else — a fraction, a negative
 * value, `Infinity`, `NaN` — is not a latency and yields `null`.
 */
function clampLatency(milliseconds: number): number | null {
  // `isInteger`, not `isSafeInteger`: past 2^53 every finite double is whole, and such a value is
  // well formed but too large, so it clamps like any other value above the cap.
  if (!Number.isInteger(milliseconds) || milliseconds < 0) {
    return null;
  }
  return Math.min(milliseconds, MAX_LATENCY_MS);
}

/** The `fixture.latency` value: decimal digits only, then the shared rule. */
function parseLatency(value: string | null): number | null {
  if (value === null || !/^\d+$/.test(value)) {
    return null;
  }
  // A digit string long enough to overflow to `Infinity` is still well formed, only enormous.
  const milliseconds = Number(value);
  return clampLatency(Number.isFinite(milliseconds) ? milliseconds : MAX_LATENCY_MS);
}

/**
 * The variant and latency overrides of the dev fixture backend, provided in the root injector so a
 * component (a review-only state switcher) and the backend share one instance.
 *
 * Every getter is a plain map read: the backend calls it on each request, so a `set…` call affects
 * the very next request and no earlier one.
 */
@Injectable({ providedIn: 'root' })
export class FixtureToggles {
  private readonly document: Document = inject(DOCUMENT);
  private readonly variants = new Map<string, string>();
  private readonly latencies = new Map<string, number>();

  constructor() {
    // `lib: dom` (not `dom.iterable`) declares `URLSearchParams.forEach` only, and the app's
    // tsconfig deliberately does not add the iterator lib for one loop.
    new URLSearchParams(this.document.location.search).forEach((value, key) => {
      if (key.startsWith(VARIANT_PARAM_PREFIX)) {
        const operationId = key.slice(VARIANT_PARAM_PREFIX.length);
        if (operationId.length > 0 && value.length > 0) {
          this.variants.set(operationId, value);
        }
        return;
      }
      if (key === LATENCY_PARAM) {
        const milliseconds = parseLatency(value);
        if (milliseconds !== null) {
          this.latencies.set(ALL_OPERATIONS, milliseconds);
        }
      }
    });
  }

  /**
   * Selects the variant the backend answers `operationId` with; `null` returns to the corpus
   * default (`ok`). The variant is not validated here: a variant no fixture carries is the
   * backend's `501 fixture_not_found`, so a typo shows as a missing fixture rather than an
   * unrelated fixture's body.
   *
   * `'*'` is a wildcard for latency only (`setLatency`, `latencyFor`). The backend asks for the
   * matched route's operation id, so a variant stored under `'*'` is never consulted: the call is
   * inert rather than a wildcard, and it changes no answer.
   */
  setVariant(operationId: string, variant: string | null): void {
    if (variant === null) {
      this.variants.delete(operationId);
      return;
    }
    this.variants.set(operationId, variant);
  }

  /**
   * Overrides the answer delay for one operation, or for `'*'` (every operation); `null` removes
   * the override. The same rule as the `fixture.latency` parameter applies (`clampLatency`): a
   * whole, non-negative number of milliseconds, clamped to ten minutes. A fraction, a negative
   * value, `Infinity` or `NaN` is not a latency and is ignored, leaving any override already set in
   * place — exactly how an unusable URL value leaves the page's toggles untouched.
   */
  setLatency(operationId: string, milliseconds: number | null): void {
    if (milliseconds === null) {
      this.latencies.delete(operationId);
      return;
    }
    const normalised = clampLatency(milliseconds);
    if (normalised === null) {
      return;
    }
    this.latencies.set(operationId, normalised);
  }

  /** The selected variant of `operationId`, or `ok` when nothing is selected. */
  variantFor(operationId: string): string {
    return this.variants.get(operationId) ?? DEFAULT_FIXTURE_VARIANT;
  }

  /** The selected latency in milliseconds, a per-operation override winning over `'*'`; `null` when unset. */
  latencyFor(operationId: string): number | null {
    return this.latencies.get(operationId) ?? this.latencies.get(ALL_OPERATIONS) ?? null;
  }
}

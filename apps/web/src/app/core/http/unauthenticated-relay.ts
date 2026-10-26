// 09.1.1-T5.2 (plan 09.1.1 §10 T5.2; 09.1.1-T3 hand-over, S61, #137): the one hook a later API call
// has into the session when the server refuses it with `401`.
//
// `ApiGateway.callWithResponse` maps a `401` to `ApiRequestError.kind === 'unauthenticated'` (an
// envelope-less 401 takes the same path). Any data service can then reject with that error; without
// this relay the page that happened to be open would render its own error pattern while the session
// was in fact gone, and the caller would never be offered a sign-in that returns to where they were.
//
// The relay is a REGISTRY, not a callback the gateway constructs: `SessionStore` registers its handler
// when it is created (root scope, once), so the gateway never resolves the store — a store -> gateway
// -> store injection cycle would otherwise be one provider away. A handler that throws cannot break
// the refusal path: the error the caller sees is always the gateway's own classification.

import { Injectable } from '@angular/core';

/**
 * What the store does with a refused request. `attemptedUrl` is the route the caller was reading,
 * as far as the reporter knows; `null` means "use the current router URL".
 */
export type UnauthenticatedHandler = (attemptedUrl: string | null) => void;

/** The one place registered session handlers live. */
@Injectable({ providedIn: 'root' })
export class UnauthenticatedRelay {
  private readonly handlers = new Set<UnauthenticatedHandler>();

  /** Registers a handler; the returned function unregisters it (a spec, not the app, needs this). */
  register(handler: UnauthenticatedHandler): () => void {
    this.handlers.add(handler);
    return () => {
      this.handlers.delete(handler);
    };
  }

  /** Tells every registered handler that a request was refused as unauthenticated. */
  notify(attemptedUrl: string | null): void {
    for (const handler of [...this.handlers]) {
      try {
        handler(attemptedUrl);
      } catch {
        // A session handler is a UI reaction; it must never replace the API's refusal.
      }
    }
  }
}

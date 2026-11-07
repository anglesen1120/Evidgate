// The one hook a later API call has into the usage summary when the server refuses it as over
// quota. A refused write is the moment the counter, and the badge built from it, are out of date;
// without this the badge would keep a value the server has already contradicted.
//
// The relay is a REGISTRY, not a callback the gateway constructs: `QuotaStore` registers its
// reload when it is created (root scope, once), so the gateway never resolves the store — a
// store -> gateway -> store injection cycle would otherwise be one provider away. A handler that
// throws cannot break the refusal path: the caller still receives the gateway's classification.

import { Injectable } from '@angular/core';

/** What a store does after a refusal: ask the server for the current counters. */
export type QuotaExceededHandler = () => void;

@Injectable({ providedIn: 'root' })
export class QuotaExceededRelay {
  private readonly handlers = new Set<QuotaExceededHandler>();

  /** Registers a handler; the returned function unregisters it (a spec, not the app, needs this). */
  register(handler: QuotaExceededHandler): () => void {
    this.handlers.add(handler);
    return () => {
      this.handlers.delete(handler);
    };
  }

  /** Tells every registered handler that a request was refused as over quota. */
  notify(): void {
    for (const handler of [...this.handlers]) {
      try {
        handler();
      } catch {
        // A store reaction is a UI refresh; it must never replace the API's refusal.
      }
    }
  }
}

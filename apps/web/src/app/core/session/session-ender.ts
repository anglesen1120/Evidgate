// 09.1.1-T5.2 (plan 09.1.1 §10 T5.2; D-S98-Q1; 02.3.2-T2): the ONE seam where this app would ask
// the server to end a session, and the reason it is a no-op today.
//
// There is no logout or session-revoke endpoint yet (`/v1/auth/logout`, `DELETE /v1/auth/sessions/…`
// land with 02.3.2-T2). The web app therefore signs out CLIENT-SIDE ONLY: the store clears every
// signal, aborts what it can, stops the session-scoped timers and keeps an in-memory latch so nothing
// re-reads the session until the caller signs in again.
//
// The browser's `__Host-evidgate_session` cookie is `HttpOnly` and stays VALID until it expires or
// 02.3.2-T2 ships: a RELOAD of the page (or a new tab) re-authenticates from that cookie, and no
// client-side code can prevent it. The login page says so in words ("Signed out on this tab"), and it
// must keep saying so — a message claiming the server session ended would be false.
//
// When 02.3.2-T2 lands, its implementation replaces this binding (an `HttpClient` `POST` whose
// `csrf_failed`/`401` outcomes are already tolerated by `SessionStore.logout`, which treats any
// rejection as "the local session is gone anyway"); nothing else in the store changes.

import { InjectionToken } from '@angular/core';

/** Ends the server-side session, if the API has a way to. */
export interface SessionEnder {
  /** Best effort: a rejection never keeps the local session alive. */
  end(): Promise<void>;
}

/**
 * The injectable seam. The default resolves without a request, which is exactly what the contract
 * offers today; a provider registered anywhere in the injector wins and no caller changes.
 */
export const SESSION_ENDER = new InjectionToken<SessionEnder>('evidgate.session-ender', {
  providedIn: 'root',
  factory: (): SessionEnder => ({ end: () => Promise.resolve() }),
});

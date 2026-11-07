// The session store's state shape and its published types. Everything here is plain data: the
// mutable bookkeeping lives in `session-context.ts` and the behaviour in the feature modules the
// store composes, so this file can be read without following any of them.

import type { StateSignals, WritableStateSource } from '@ngrx/signals';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

/** What the shell knows about the caller's session. */
export type SessionState =
  /** Nothing has been read yet; `bootstrap()` settles it. */
  | 'unknown'
  /** The API answered with the caller's organizations and the permission source resolved. */
  | 'authenticated'
  /** The API refused with `401`: there is no usable session. */
  | 'anonymous'
  /** The session could not be read: a transport failure, a 5xx, a rate limit, an unknown error. */
  | 'unavailable';

/** The outcome of resolving an org/project pair from the URL. */
export type ScopeResolution =
  /** The pair is one the server listed; `currentOrg`/`currentProject` now describe it. */
  | 'ok'
  /** Unknown, foreign, or not listed for this caller — deliberately one answer. */
  | 'not-found'
  /**
   * A newer `resolveScope` (or a `reload()`) took the scope while this one awaited. The answer
   * describes a request nobody is waiting for any more, so it is neither a match nor a claim that
   * the id is unknown: the guard that receives it cancels instead of rendering the 404 pattern.
   */
  | 'superseded'
  /** The check itself could not be completed (transport, `5xx`, rate limit, programming error). */
  | 'unavailable';

/** Never-mutated empty values: an empty permission set grants nothing, which is the safe default. */
export const NO_PERMISSIONS: ReadonlySet<string> = new Set<string>();
export const NO_ORGANIZATIONS: readonly OrganizationSummaryDto[] = [];
export const NO_PROJECTS: readonly ProjectDto[] = [];

/**
 * The store's state. Every field is published as one read-only signal of the same name, which is
 * exactly the surface the guards, the sidebar and the pages read: the conversion from the
 * hand-rolled signals class changed no name.
 */
export type SessionStoreState = {
  /** `unknown` until the first `bootstrap()` settles it. */
  readonly sessionState: SessionState;
  /**
   * The in-memory sign-out latch: `true` from `logout()` (or an expiry redirect) until `reauth()`.
   * While it is set, `bootstrap()` and `reload()` answer `anonymous` WITHOUT a request, so a late
   * probe can never resurrect the session the caller just ended. It is deliberately not persisted:
   * a reload re-authenticates from the still-valid `HttpOnly` cookie (`session-ender.ts`).
   */
  readonly signedOut: boolean;
  /** The codes the shell treats as granted; empty until the session is authenticated. */
  readonly permissions: ReadonlySet<string>;
  /** Every organization the server listed for this caller, in server order. */
  readonly organizations: readonly OrganizationSummaryDto[];
  /** The projects of {@link currentOrg}; empty when no org is resolved. */
  readonly projects: readonly ProjectDto[];
  /** The org the route resolved to, or `null`. */
  readonly currentOrg: OrganizationSummaryDto | null;
  /** The project the route resolved to, or `null` for an org-scope route. */
  readonly currentProject: ProjectDto | null;
};

export const INITIAL_STATE: SessionStoreState = {
  sessionState: 'unknown',
  signedOut: false,
  permissions: NO_PERMISSIONS,
  organizations: NO_ORGANIZATIONS,
  projects: NO_PROJECTS,
  currentOrg: null,
  currentProject: null,
};

/**
 * What a feature module receives of the store: the state signals it reads plus the writable source
 * `patchState` needs. Modules never see the whole instance, so they cannot reach another feature's
 * methods by accident.
 */
export type SessionStoreHandle = StateSignals<SessionStoreState> &
  WritableStateSource<SessionStoreState>;

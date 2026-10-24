// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4, §5; D-S68-Q2): the shell's session and scope state as a
// plain Angular signals service — README and the plan name `@ngrx/signals`, but the workspace does
// not depend on it, so the store is shaped like a `signalStore` (readonly signals out, methods in)
// without the package: private `signal`s, public `asReadonly()`/`computed`, no writable state
// escaping the class.
//
// Two facts drive every method:
//
// 1. The URL is a request, never evidence. `resolveScope` answers only from lists the server
//    returned: an org must be one of `GET /v1/orgs`, a project must come from
//    `GET /v1/orgs/{o}/projects` *and* carry the same `org_id`. A foreign id is `not-found`, which
//    the scope guards turn into a non-match so the 404 pattern renders — never a 403, which would
//    confirm the id exists in another tenant (00 §9.2, D-S68-Q4).
// 2. The permission set is a convenience (D-S68-Q1): it holds only server-provided codes and an
//    empty set grants nothing. The API's own `403` stays the only real gate.

import { inject, Injectable, signal } from '@angular/core';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import { IdentityDataService } from '../data/identity-data.service';
import { isApiRequestError } from '../http/api-error';
import {
  CataloguePermissionsSource,
  EffectivePermissionsSource,
} from './effective-permissions.source';

/** What the shell knows about the caller's session (00 §8.9). */
export type SessionState =
  /** Nothing has been read yet; `bootstrap()` settles it. */
  | 'unknown'
  /** The API answered with the caller's organizations and the permission source resolved. */
  | 'authenticated'
  /** The API refused with `401`: there is no usable session (00 §9.2). */
  | 'anonymous'
  /** The session could not be read: a transport failure, a 5xx, a rate limit, an unknown error. */
  | 'unavailable';

/** The outcome of resolving an org/project pair from the URL. */
export type ScopeResolution =
  /** The pair is one the server listed; `currentOrg`/`currentProject` now describe it. */
  | 'ok'
  /** Unknown, foreign, or not listed for this caller — deliberately one answer (00 §9.2). */
  | 'not-found'
  /**
   * A newer `resolveScope` (or a `reload()`) took the scope while this one awaited. The answer
   * describes a request nobody is waiting for any more, so it is neither a match nor a claim that
   * the id is unknown: the guard that receives it cancels instead of rendering the 404 pattern.
   */
  | 'superseded'
  /** The check itself could not be completed (transport, `5xx`, rate limit, programming error). */
  | 'unavailable';

/** The frozen empty set: `has()` on it answers `false` for every code, which is the safe default. */
const NO_PERMISSIONS: ReadonlySet<string> = new Set<string>();
const NO_ORGANIZATIONS: readonly OrganizationSummaryDto[] = [];
const NO_PROJECTS: readonly ProjectDto[] = [];

/** One side of the concurrent session read: its value, or the failure it answered with. */
type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

/**
 * Settles one independent session read. `readSession` runs the organization list and the
 * permission source side by side, and each has to fail on its own so the other one's `401` can
 * still be seen: `Promise.all` would surface only the first rejection.
 */
async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}

@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly identity = inject(IdentityDataService);

  /**
   * The effective-grant seam (D-S68-Q1). The token is optional so the app boots with no binding:
   * until 02.3.1 provides the caller's real grants, the catalogue default answers. A replacement
   * registered anywhere in the injector wins, and this class stays unchanged.
   */
  private readonly permissionsSource =
    inject(EffectivePermissionsSource, { optional: true }) ?? inject(CataloguePermissionsSource);

  private readonly sessionSignal = signal<SessionState>('unknown');
  private readonly permissionsSignal = signal<ReadonlySet<string>>(NO_PERMISSIONS);
  private readonly organizationsSignal =
    signal<readonly OrganizationSummaryDto[]>(NO_ORGANIZATIONS);
  private readonly projectsSignal = signal<readonly ProjectDto[]>(NO_PROJECTS);
  private readonly orgSignal = signal<OrganizationSummaryDto | null>(null);
  private readonly projectSignal = signal<ProjectDto | null>(null);

  /** `unknown` until the first `bootstrap()` settles it. */
  readonly sessionState = this.sessionSignal.asReadonly();
  /** The codes the shell treats as granted; empty until the session is authenticated. */
  readonly permissions = this.permissionsSignal.asReadonly();
  /** Every organization the server listed for this caller, in server order. */
  readonly organizations = this.organizationsSignal.asReadonly();
  /** The projects of {@link currentOrg}; empty when no org is resolved. */
  readonly projects = this.projectsSignal.asReadonly();
  /** The org the route resolved to, or `null`. */
  readonly currentOrg = this.orgSignal.asReadonly();
  /** The project the route resolved to, or `null` for an org-scope route. */
  readonly currentProject = this.projectSignal.asReadonly();

  /**
   * The project list already read for the org it belongs to, published only for `org_id`s the
   * server attributed to that org. `resolveScope` answers from here when the route asks for the
   * org that is already current, so a navigation inside an org reads nothing and blanks nothing.
   * A real org change drops it before reading the new org, and `clearScope` drops it on every
   * failure, every `not-found` and `reload()`, so it never outlives the session it was read under.
   */
  private readonly projectsByOrg = new Map<string, readonly ProjectDto[]>();

  /** The single in-flight bootstrap, so a burst of guards awaits one read instead of N. */
  private inFlight: Promise<void> | null = null;
  /** Bumped by `reload()`; a bootstrap of an older generation never writes state. */
  private sessionRevision = 0;
  /** Bumped by every `resolveScope` call and by `reload()`; guards the async part of a scope. */
  private scopeRevision = 0;

  /**
   * Reads the session once (00 §8.9). Idempotent: a settled state is returned as-is and concurrent
   * callers share one in-flight read, so every guard may await it unconditionally.
   *
   * The two reads are independent — the organization list is not a prerequisite of the permission
   * catalogue — so they run together, and a `401` from EITHER of them is the one refusal that means
   * the caller has no session (`readSession` maps a pair of failures accordingly).
   */
  bootstrap(): Promise<void> {
    if (this.inFlight !== null) {
      return this.inFlight;
    }
    if (this.sessionSignal() !== 'unknown') {
      return Promise.resolve();
    }
    const run: Promise<void> = this.readSession(this.sessionRevision).finally(() => {
      if (this.inFlight === run) {
        this.inFlight = null;
      }
    });
    this.inFlight = run;
    return run;
  }

  /**
   * Forgets the session and reads it again, for the service-unavailable page's retry and for a
   * later sign-in. In-flight reads of the previous generation are ignored when they settle.
   *
   * Unlike `bootstrap()`, this rejects when the retry ends unreadable: a guard must never throw,
   * but the retry button has to know whether it worked (`ServiceUnavailablePage` shows its failure
   * copy from this rejection; without it a failed retry would navigate and bounce straight back).
   * An `anonymous` outcome resolves — "no session" is an answer, not a failed read.
   */
  async reload(): Promise<void> {
    this.sessionRevision += 1;
    this.inFlight = null;
    this.sessionSignal.set('unknown');
    this.clearSession();
    await this.bootstrap();
    if (this.sessionSignal() === 'unavailable') {
      throw new Error('the workspace is still unavailable');
    }
  }

  /**
   * Resolves the URL's org/project pair against what the server listed and makes it the current
   * scope. Never trusts an id: a foreign or unknown org, a project of another org, or a project the
   * server did not list is `not-found`, and the scope is cleared so nothing stale stays rendered.
   *
   * A route chain asks more than once per navigation (`orgScopeGuard`, then `projectScopeGuard`,
   * then the landing guard), and every sidebar click is another navigation inside the org already
   * resolved. So the project list read for an org is cached: when the target org is the current one
   * and its list is already loaded, neither the signals nor the network are touched, and the
   * project is answered from the cache. That keeps `currentOrg()`/`projects()` populated for the
   * whole navigation — the sidebar and the project switcher never blink empty — and keeps one
   * project-list read per navigation instead of one per guard. A real org change, a failure and
   * `reload()` all drop the cache and read again.
   *
   * A resolution that a newer one (or a `reload()`) overtook while it awaited answers
   * `superseded`, not `not-found`: the id was never shown to be unknown, and the guard that
   * receives it must cancel rather than render the 404 pattern.
   */
  async resolveScope(orgId: string, projectId: string | null): Promise<ScopeResolution> {
    await this.bootstrap();
    if (this.sessionSignal() === 'unavailable') {
      return 'unavailable';
    }
    const revision = ++this.scopeRevision;
    if (this.sessionSignal() !== 'authenticated') {
      // No session, no scope; `not-found` never confirms that the id exists anywhere.
      this.clearScope();
      return 'not-found';
    }

    const org = this.organizationsSignal().find((candidate) => candidate.id === orgId);
    if (org === undefined) {
      this.clearScope();
      return 'not-found';
    }

    // The org is already current and its list is loaded: answer from the cache. `undefined` means
    // this org's list was never read, or a failure dropped it.
    const cached = this.orgSignal()?.id === orgId ? this.projectsByOrg.get(orgId) : undefined;
    if (cached !== undefined) {
      const project =
        projectId === null
          ? null
          : cached.find((candidate) => candidate.id === projectId && candidate.org_id === orgId);
      if (projectId !== null && project === undefined) {
        this.clearScope();
        return 'not-found';
      }
      this.projectSignal.set(project ?? null);
      return 'ok';
    }

    // A real org change: the previous org's list is not this one's, and refilling it is one read.
    if (this.orgSignal()?.id !== orgId) {
      this.projectsByOrg.clear();
    }

    // The first resolve of this org, a real org change, or a retry after a failure. The previous
    // scope is dropped before the await: a slow project list must not keep the old org's projects
    // (and therefore the old org's navigation links) on screen.
    this.orgSignal.set(org);
    this.projectSignal.set(null);
    this.projectsSignal.set(NO_PROJECTS);

    let projects: readonly ProjectDto[];
    try {
      projects = await this.identity.listProjects(orgId);
    } catch (error) {
      if (revision === this.scopeRevision) {
        this.clearScope();
      }
      return scopeFailure(error);
    }
    if (revision !== this.scopeRevision) {
      // A newer navigation owns the scope by now; this answer is about a request nobody waits for.
      return 'superseded';
    }

    // Only the rows the server attributed to THIS org. The endpoint is asked per org, but the
    // published list is what the switcher offers and what a landing address is built from, so a
    // row carrying another `org_id` must never reach it — the explicit-id branch below refuses the
    // same row, and one answer must not contradict the other.
    const owned = projects.filter((candidate) => candidate.org_id === orgId);
    this.projectsByOrg.set(orgId, owned);
    this.projectsSignal.set(owned);

    if (projectId === null) {
      return 'ok';
    }
    const project = owned.find((candidate) => candidate.id === projectId);
    if (project === undefined) {
      this.clearScope();
      return 'not-found';
    }
    this.projectSignal.set(project);
    return 'ok';
  }

  /** `true` only for a code the source put in the set — an empty set grants nothing. */
  has(code: string): boolean {
    return this.permissionsSignal().has(code);
  }

  /**
   * One round trip of organizations and permissions; a stale generation writes nothing.
   *
   * The two reads are independent (only the caller's session joins them), so they run together: the
   * catalogue is up to twenty cursor pages and must not sit behind the organization list.
   *
   * A `401` from EITHER read is the one refusal with a UI state of its own (`anonymous`) and wins
   * over the other read's failure — an expired session is an answer, not an outage. Everything else
   * — a `5xx`, a dropped connection, a broken cursor loop, a missing provider — means "could not
   * read", which the shell renders as unavailable rather than as an empty, silently degraded
   * workspace. Any failure also drops the cached project lists, so a later resolve cannot publish
   * a list read under a session that is no longer confirmed.
   */
  private async readSession(revision: number): Promise<void> {
    const [organizations, permissions] = await Promise.all([
      settledRead(() => this.identity.listMyOrganizations()),
      settledRead(() => this.permissionsSource.load()),
    ]);
    if (revision !== this.sessionRevision) {
      return;
    }
    if (!organizations.ok || !permissions.ok) {
      const failures = [organizations, permissions].flatMap((read) =>
        read.ok ? [] : [read.error],
      );
      this.projectsByOrg.clear();
      const sessionGone = failures.some(
        (error) => isApiRequestError(error) && error.error.kind === 'unauthenticated',
      );
      this.sessionSignal.set(sessionGone ? 'anonymous' : 'unavailable');
      return;
    }
    this.organizationsSignal.set(organizations.value);
    this.permissionsSignal.set(permissions.value);
    this.sessionSignal.set('authenticated');
  }

  /** Forgets everything a `reload()` must not keep. */
  private clearSession(): void {
    this.permissionsSignal.set(NO_PERMISSIONS);
    this.organizationsSignal.set(NO_ORGANIZATIONS);
    this.clearScope();
  }

  /** Forgets the resolved pair, its project list, and every cached project list. */
  private clearScope(): void {
    this.scopeRevision += 1;
    this.orgSignal.set(null);
    this.projectSignal.set(null);
    this.projectsSignal.set(NO_PROJECTS);
    this.projectsByOrg.clear();
  }
}

/**
 * A failed scope lookup. A refusal the server expressed about the resource (`404`, and a `403` for
 * a caller who may not read this org's projects) is indistinguishable from an id that does not
 * exist — 00 §9.2 wants exactly that — so both answer `not-found` and the wildcard route renders
 * the same 404 pattern. Everything else means the check could not be completed: `unavailable`.
 */
function scopeFailure(error: unknown): ScopeResolution {
  if (
    isApiRequestError(error) &&
    (error.error.kind === 'not_found' || error.error.kind === 'forbidden')
  ) {
    return 'not-found';
  }
  return 'unavailable';
}

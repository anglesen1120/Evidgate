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
  /** The check itself could not be completed (transport, `5xx`, rate limit, programming error). */
  | 'unavailable';

/** The frozen empty set: `has()` on it answers `false` for every code, which is the safe default. */
const NO_PERMISSIONS: ReadonlySet<string> = new Set<string>();
const NO_ORGANIZATIONS: readonly OrganizationSummaryDto[] = [];
const NO_PROJECTS: readonly ProjectDto[] = [];

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
   * Order matters: organizations first — a `401` there is the only signal that the caller has no
   * session — then the permission source, because the catalogue operation itself needs a session.
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

    // The previous scope is dropped before the await: a slow project list must not keep the old
    // org's projects (and therefore the old org's navigation links) on screen.
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
      // A newer navigation owns the scope by now; this answer is stale.
      return 'not-found';
    }

    if (projectId === null) {
      this.projectsSignal.set(projects);
      return 'ok';
    }

    const project = projects.find(
      (candidate) => candidate.id === projectId && candidate.org_id === orgId,
    );
    if (project === undefined) {
      this.clearScope();
      return 'not-found';
    }
    this.projectsSignal.set(projects);
    this.projectSignal.set(project);
    return 'ok';
  }

  /** `true` only for a code the source put in the set — an empty set grants nothing. */
  has(code: string): boolean {
    return this.permissionsSignal().has(code);
  }

  /** One read of organizations then permissions; a stale generation writes nothing. */
  private async readSession(revision: number): Promise<void> {
    try {
      const organizations = await this.identity.listMyOrganizations();
      if (revision !== this.sessionRevision) {
        return;
      }
      const permissions = await this.permissionsSource.load();
      if (revision !== this.sessionRevision) {
        return;
      }
      this.organizationsSignal.set(organizations);
      this.permissionsSignal.set(permissions);
      this.sessionSignal.set('authenticated');
    } catch (error) {
      if (revision !== this.sessionRevision) {
        return;
      }
      // A 401 is the one refusal with a UI state of its own; everything else — a 5xx, a dropped
      // connection, a broken cursor loop, a missing provider — means "could not read", which the
      // shell renders as unavailable rather than as an empty, silently degraded workspace.
      this.sessionSignal.set(
        isApiRequestError(error) && error.error.kind === 'unauthenticated'
          ? 'anonymous'
          : 'unavailable',
      );
    }
  }

  /** Forgets everything a `reload()` must not keep. */
  private clearSession(): void {
    this.permissionsSignal.set(NO_PERMISSIONS);
    this.organizationsSignal.set(NO_ORGANIZATIONS);
    this.clearScope();
  }

  /** Forgets the resolved pair and its project list. */
  private clearScope(): void {
    this.scopeRevision += 1;
    this.orgSignal.set(null);
    this.projectSignal.set(null);
    this.projectsSignal.set(NO_PROJECTS);
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

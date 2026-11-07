import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';
import { vi } from 'vitest';

import { ApiRequestError, type ApiErrorKind } from '../http/api-error';
import { IdentityDataService } from '../data/identity-data.service';
import { EffectivePermissionsSource } from './effective-permissions.source';
import { SESSION_ENDER, type SessionEnder } from './session-ender';
import { SessionStore } from './session.store';
import { TenantStore } from './tenant.store';

export function apiError(kind: ApiErrorKind, status: number): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `${kind} (test)`,
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

export const ORG_ONE: OrganizationSummaryDto = {
  id: 'o-1',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};
export const ORG_TWO: OrganizationSummaryDto = {
  id: 'o-2',
  slug: 'lumen-labs',
  name: 'Lumen Labs',
  status: 'active',
};

export function project(id: string, orgId: string): ProjectDto {
  return {
    id,
    org_id: orgId,
    name: `Project ${id}`,
    slug: `project-${id}`,
    status: 'active',
    default_environment_id: null,
    created_at: '2026-10-02T09:00:00Z',
    updated_at: '2026-10-18T11:20:00Z',
    version: 1,
    retention_days: null,
    monthly_run_quota: null,
    case_prefix: 'PROJ',
  };
}

export const PROJECT_P1 = project('p-1', 'o-1');
export const PROJECT_P2 = project('p-2', 'o-1');
export const PROJECT_Q1 = project('q-1', 'o-2');

/** One project-list read the store is waiting on, for the out-of-order scope case. */
export interface PendingProjects {
  readonly orgId: string;
  readonly resolve: (projects: readonly ProjectDto[]) => void;
}

/** The store never reads the catalogue directly: that is the permission source's job. */
export class FakeIdentityDataService {
  organizations: readonly OrganizationSummaryDto[] = [];
  organizationFailure: Error | null = null;
  projectsByOrg = new Map<string, readonly ProjectDto[]>();
  projectsFailure: Error | null = null;
  /** When true, `listProjects` answers only when the test settles {@link pending}. */
  manualProjects = false;
  /** When true, `listMyOrganizations` answers only when the test settles {@link pendingOrgs}. */
  manualOrganizations = false;
  organizationCalls = 0;
  projectOrgIds: string[] = [];
  readonly pending: PendingProjects[] = [];
  readonly pendingOrgs: Array<(organizations: readonly OrganizationSummaryDto[]) => void> = [];

  listMyOrganizations(): Promise<readonly OrganizationSummaryDto[]> {
    this.organizationCalls += 1;
    if (this.manualOrganizations) {
      // `Promise.withResolvers` is not in this project's lib target (ES2022).
      return new Promise<readonly OrganizationSummaryDto[]>((resolve) => {
        this.pendingOrgs.push(resolve);
      });
    }
    return this.organizationFailure === null
      ? Promise.resolve(this.organizations)
      : Promise.reject(this.organizationFailure);
  }

  listProjects(orgId: string): Promise<readonly ProjectDto[]> {
    this.projectOrgIds.push(orgId);
    if (this.manualProjects) {
      return new Promise<readonly ProjectDto[]>((resolve) => {
        this.pending.push({ orgId, resolve });
      });
    }
    return this.projectsFailure === null
      ? Promise.resolve(this.projectsByOrg.get(orgId) ?? [])
      : Promise.reject(this.projectsFailure);
  }

  listPermissions(): Promise<never> {
    throw new Error('the session store must not read the catalogue directly');
  }
}

/** The granted set the store consumes, plus the calls it observed. */
export class FakePermissionsSource {
  granted: ReadonlySet<string> = new Set<string>();
  failure: Error | null = null;
  calls = 0;
  /** Permission reads are tenant-scoped, so record the org published before each load. */
  tenantAtLoad: (() => string | null) | null = null;
  readonly tenants: Array<string | null> = [];
  /** When true, `load` answers only when the test settles {@link pendingLoads}. */
  manual = false;
  readonly pendingLoads: Array<{
    readonly resolve: (granted: ReadonlySet<string>) => void;
    readonly reject: (error: unknown) => void;
  }> = [];

  load(): Promise<ReadonlySet<string>> {
    this.calls += 1;
    this.tenants.push(this.tenantAtLoad?.() ?? null);
    if (this.manual) {
      // This project's lib is ES2022, so `Promise.withResolvers` is unavailable.
      return new Promise<ReadonlySet<string>>((resolve, reject) => {
        this.pendingLoads.push({ resolve, reject });
      });
    }
    return this.failure === null ? Promise.resolve(this.granted) : Promise.reject(this.failure);
  }
}

/** A failing server-side end must never keep the tab signed in. */
export class FakeSessionEnder implements SessionEnder {
  calls = 0;
  rejection: Error | null = null;

  end(): Promise<void> {
    this.calls += 1;
    return this.rejection === null ? Promise.resolve() : Promise.reject(this.rejection);
  }
}

/** Lets every pending microtask (the store's awaits) run before the next assertion. */
export async function settle(): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

export function setupSessionStore() {
  const identity = new FakeIdentityDataService();
  const source = new FakePermissionsSource();
  const ender = new FakeSessionEnder();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: IdentityDataService, useValue: identity },
      { provide: EffectivePermissionsSource, useValue: source },
      { provide: SESSION_ENDER, useValue: ender },
    ],
  });
  const store = TestBed.inject(SessionStore);
  const tenant = TestBed.inject(TenantStore);
  source.tenantAtLoad = () => tenant.orgId();
  const router = TestBed.inject(Router);
  // The two session boundaries leave through `/login`; a spec records the decision instead of
  // running a navigation through an empty route table. Recording (rather than asserting on the
  // spy object) keeps the expected call shape readable as data.
  const navigations: Array<{ commands: readonly unknown[]; extras: unknown }> = [];
  vi.spyOn(router, 'navigate').mockImplementation((commands, extras) => {
    navigations.push({ commands: commands as readonly unknown[], extras });
    return Promise.resolve(true);
  });
  return { identity, source, ender, router, navigations, store, tenant };
}

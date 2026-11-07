import { effect } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { IdentityDataService } from '../data/identity-data.service';
import { apiError } from '../../shared/testing/http-data.testing';
import { EffectivePermissionsSource } from './effective-permissions.source';
import { SessionStore } from './session.store';
import {
  ORG_ONE,
  ORG_TWO,
  PROJECT_P1,
  PROJECT_P2,
  PROJECT_Q1,
  settle,
  FakeIdentityDataService,
  FakePermissionsSource,
  setupSessionStore,
} from './session.store.test-support';

describe('SessionStore (scope)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let store: SessionStore;

  beforeEach(() => {
    ({ identity, source, store } = setupSessionStore());
  });

  describe('resolveScope', () => {
    beforeEach(async () => {
      identity.organizations = [ORG_ONE, ORG_TWO];
      identity.projectsByOrg.set('o-1', [PROJECT_P1, PROJECT_P2]);
      identity.projectsByOrg.set('o-2', [PROJECT_Q1]);
      source.granted = new Set(['run:read']);
      await store.bootstrap();
    });

    it('resolves an org and its project from the server lists', async () => {
      const resolution = await store.resolveScope('o-1', 'p-2');

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_ONE);
      expect(store.currentProject()).toEqual(PROJECT_P2);
      expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
    });

    it('resolves an org with no project for an org-scope route', async () => {
      const resolution = await store.resolveScope('o-1', null);

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_ONE);
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
    });

    it('refuses an org the server did not list and reads no project list', async () => {
      const resolution = await store.resolveScope('o-foreign', 'p-1');

      expect(resolution).toBe('not-found');
      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([]);
      expect(identity.projectOrgIds).toEqual([]);
    });

    it('refuses a project id the org does not have', async () => {
      const resolution = await store.resolveScope('o-1', 'p-unknown');

      expect(resolution).toBe('not-found');
      expect(store.currentOrg()).toBeNull();
      expect(store.currentProject()).toBeNull();
      expect(store.projects()).toEqual([]);
    });

    it('refuses a project the server returned with another org_id', async () => {
      identity.projectsByOrg.set('o-1', [{ ...PROJECT_Q1, id: 'p-9' }]);

      const resolution = await store.resolveScope('o-1', 'p-9');

      expect(resolution).toBe('not-found');
      expect(store.currentProject()).toBeNull();
    });

    it('drops a previous scope before resolving the next one', async () => {
      await store.resolveScope('o-1', 'p-1');
      expect(store.currentProject()).toEqual(PROJECT_P1);

      const resolution = await store.resolveScope('o-2', 'q-1');

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.currentProject()).toEqual(PROJECT_Q1);
      expect(store.projects()).toEqual([PROJECT_Q1]);
    });

    it('refuses a scope while the session is anonymous, without reading projects', async () => {
      TestBed.resetTestingModule();
      const anonymousIdentity = new FakeIdentityDataService();
      anonymousIdentity.organizationFailure = apiError('unauthenticated', 401);
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: IdentityDataService, useValue: anonymousIdentity },
          { provide: EffectivePermissionsSource, useValue: new FakePermissionsSource() },
        ],
      });
      const anonymousStore = TestBed.inject(SessionStore);

      const resolution = await anonymousStore.resolveScope('o-1', 'p-1');

      expect(resolution).toBe('not-found');
      expect(anonymousIdentity.projectOrgIds).toEqual([]);
      expect(anonymousStore.sessionState()).toBe('anonymous');
    });

    it('answers unavailable when the scope is unreadable', async () => {
      identity.projectsFailure = apiError('unavailable', 503);

      const resolution = await store.resolveScope('o-1', 'p-1');

      expect(resolution).toBe('unavailable');
      expect(store.currentOrg()).toBeNull();
    });

    it('answers not-found for a project list refusal, which 00 §9.2 makes indistinguishable', async () => {
      identity.projectsFailure = apiError('not_found', 404);

      const resolution = await store.resolveScope('o-1', 'p-1');

      expect(resolution).toBe('not-found');
    });

    it('answers not-found for a forbidden project list, never confirming the org exists', async () => {
      identity.projectsFailure = apiError('forbidden', 403);

      const resolution = await store.resolveScope('o-1', null);

      expect(resolution).toBe('not-found');
    });

    it('lets the newest navigation win when two resolutions overlap', async () => {
      identity.manualProjects = true;

      const first = store.resolveScope('o-1', 'p-1');
      await settle();
      const second = store.resolveScope('o-2', 'q-1');
      await settle();
      expect(identity.pending.map((entry) => entry.orgId)).toEqual(['o-1', 'o-2']);

      const newest = identity.pending[1];
      const older = identity.pending[0];
      if (newest === undefined || older === undefined) {
        throw new Error('both scope reads must have reached the fake');
      }
      newest.resolve([PROJECT_Q1]);
      expect(await second).toBe('ok');
      // The stale answer arrives last and must change nothing — and it must not claim that the
      // valid org it was asked about is unknown.
      older.resolve([PROJECT_P1, PROJECT_P2]);
      expect(await first).toBe('superseded');

      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.currentProject()).toEqual(PROJECT_Q1);
      expect(store.projects()).toEqual([PROJECT_Q1]);
    });

    it('publishes only the rows the server attributed to the resolved org', async () => {
      // The fixture corpus answers any org's project request with org 1's rows, and a server that
      // leaks another org's row must not make the switcher or a landing address use it.
      identity.projectsByOrg.set('o-2', [PROJECT_P1, PROJECT_Q1]);

      const resolution = await store.resolveScope('o-2', null);

      expect(resolution).toBe('ok');
      expect(store.currentOrg()).toEqual(ORG_TWO);
      expect(store.projects()).toEqual([PROJECT_Q1]);
      // The explicit-id branch refuses the same foreign row, so one answer never contradicts the
      // other: the row is not a project of this org either way.
      expect(await store.resolveScope('o-2', PROJECT_P1.id)).toBe('not-found');
    });

    describe('inside the org already resolved', () => {
      // A same-org navigation must not pass through a state where the sidebar has no org
      // or projects to render.
      function recordScope(): Array<{
        readonly org: string | null;
        readonly projects: number;
        readonly project: string | null;
      }> {
        const seen: Array<{ org: string | null; projects: number; project: string | null }> = [];
        TestBed.runInInjectionContext(() => {
          effect(() => {
            seen.push({
              org: store.currentOrg()?.id ?? null,
              projects: store.projects().length,
              project: store.currentProject()?.id ?? null,
            });
          });
        });
        return seen;
      }

      it('reads the project list once for the whole navigation and never blanks the scope', async () => {
        const seen = recordScope();
        // The first run records the state before any resolve; later runs record each settle.
        TestBed.tick();

        // `/orgs/o-1/projects/p-1/<segment>`: the org guard, then the project guard.
        await store.resolveScope('o-1', null);
        TestBed.tick();
        await store.resolveScope('o-1', 'p-1');
        TestBed.tick();

        expect(identity.projectOrgIds).toEqual(['o-1']);
        expect(seen).toEqual([
          { org: null, projects: 0, project: null },
          { org: 'o-1', projects: 2, project: null },
          { org: 'o-1', projects: 2, project: 'p-1' },
        ]);
      });

      it('answers a second navigation inside the org from the loaded list, without a read', async () => {
        await store.resolveScope('o-1', 'p-1');

        expect(await store.resolveScope('o-1', 'p-2')).toBe('ok');

        expect(identity.projectOrgIds).toEqual(['o-1']);
        expect(store.currentProject()).toEqual(PROJECT_P2);
        expect(store.projects()).toEqual([PROJECT_P1, PROJECT_P2]);
      });

      it('refuses a project that is not in the loaded list without reading it again', async () => {
        await store.resolveScope('o-1', 'p-1');

        expect(await store.resolveScope('o-1', 'p-unknown')).toBe('not-found');

        expect(identity.projectOrgIds).toEqual(['o-1']);
        expect(store.currentOrg()).toBeNull();
      });

      it('re-reads the project list when the org really changes', async () => {
        await store.resolveScope('o-1', 'p-1');

        expect(await store.resolveScope('o-2', 'q-1')).toBe('ok');

        expect(identity.projectOrgIds).toEqual(['o-1', 'o-2']);
        expect(store.currentOrg()).toEqual(ORG_TWO);
        expect(store.projects()).toEqual([PROJECT_Q1]);
      });

      it('drops the cache so reload() reads the list again', async () => {
        await store.resolveScope('o-1', null);

        await store.reload();
        await store.resolveScope('o-1', null);

        expect(identity.projectOrgIds).toEqual(['o-1', 'o-1']);
      });

      it('re-reads the list after a reload that recovered from a failed session read', async () => {
        await store.resolveScope('o-1', null);
        identity.organizationFailure = apiError('unavailable', 503);

        await expect(store.reload()).rejects.toThrow('still unavailable');
        expect(store.sessionState()).toBe('unavailable');

        identity.organizationFailure = null;
        await store.reload();

        expect(await store.resolveScope('o-1', null)).toBe('ok');
        // The failed generation and the recovered one both read the list: nothing from the old
        // session may survive into the new one.
        expect(identity.projectOrgIds).toEqual(['o-1', 'o-1']);
      });
    });
  });
});

import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { IdentityDataService } from '../data/identity-data.service';
import { EffectivePermissionsSource } from './effective-permissions.source';
import { SessionStore } from './session.store';
import {
  ORG_ONE,
  ORG_TWO,
  project,
  PROJECT_P1,
  PROJECT_Q1,
  FakeIdentityDataService,
  FakePermissionsSource,
  setupSessionStore,
} from './session.store.test-support';

describe('SessionStore (adopt-has)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let store: SessionStore;

  beforeEach(() => {
    ({ identity, source, store } = setupSessionStore());
  });

  describe('adoptCreatedProject (09.2.1 T4 delta N2)', () => {
    beforeEach(async () => {
      identity.organizations = [ORG_ONE, ORG_TWO];
      identity.projectsByOrg.set('o-1', [PROJECT_P1]);
      identity.projectsByOrg.set('o-2', [PROJECT_Q1]);
      await store.bootstrap();
      await store.resolveScope('o-1', null);
    });

    it('appends the created project to the loaded list of the current org, with no read', async () => {
      const created = project('p-new', 'o-1');

      store.adoptCreatedProject(created);

      expect(store.projects()).toEqual([PROJECT_P1, created]);
      // Adopting is not a navigation: the scope the wizard was on stays where it was.
      expect(store.currentProject()).toBeNull();
      expect(identity.projectOrgIds).toEqual(['o-1']);

      // The wizard redirects to the new project id, and the guards ask about it: the answer comes
      // from the list that now holds it, so the redirect does not land on the 404 pattern.
      expect(await store.resolveScope('o-1', created.id)).toBe('ok');
      expect(store.currentProject()).toEqual(created);
      // The cache, not just the published signal, was updated: a later same-org resolve reads
      // nothing either.
      expect(await store.resolveScope('o-1', null)).toBe('ok');
      expect(identity.projectOrgIds).toEqual(['o-1']);
      expect(store.projects()).toEqual([PROJECT_P1, created]);
    });

    it('appends a created project once, however often it is adopted', async () => {
      const created = project('p-new', 'o-1');

      store.adoptCreatedProject(created);
      store.adoptCreatedProject(created);

      expect(store.projects()).toEqual([PROJECT_P1, created]);
      expect(await store.resolveScope('o-1', created.id)).toBe('ok');
      expect(store.currentProject()).toEqual(created);
    });

    it('does not append an id the loaded list already holds', () => {
      store.adoptCreatedProject(PROJECT_P1);

      expect(store.projects()).toEqual([PROJECT_P1]);
    });

    it('publishes nothing for a project of another org and reads that org before answering', async () => {
      store.adoptCreatedProject(project('q-new', 'o-2'));

      // The current scope is org one, so its list is what the switcher offers.
      expect(store.projects()).toEqual([PROJECT_P1]);
      expect(store.currentOrg()).toEqual(ORG_ONE);

      // The current org's own list is untouched: adopting a foreign project is not a reason to
      // make the next same-org navigation read the list again.
      expect(await store.resolveScope('o-1', 'p-1')).toBe('ok');
      expect(identity.projectOrgIds).toEqual(['o-1']);

      // The adopted row must never answer for org two out of the store: the first resolve of that
      // org reads the server, so an id the server did not list is `not-found`, not a match.
      expect(await store.resolveScope('o-2', 'q-new')).toBe('not-found');
      expect(identity.projectOrgIds).toEqual(['o-1', 'o-2']);
    });

    it('publishes nothing when the org list was never read, and fabricates no cache entry', async () => {
      // A session that only knows the org — step one of the wizard — has no project list at all.
      TestBed.resetTestingModule();
      const freshIdentity = new FakeIdentityDataService();
      freshIdentity.organizations = [ORG_ONE];
      freshIdentity.projectsByOrg.set('o-1', [PROJECT_P1]);
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: IdentityDataService, useValue: freshIdentity },
          { provide: EffectivePermissionsSource, useValue: new FakePermissionsSource() },
        ],
      });
      const freshStore = TestBed.inject(SessionStore);
      await freshStore.bootstrap();

      freshStore.adoptCreatedProject(project('p-unread', 'o-1'));

      expect(freshStore.projects()).toEqual([]);
      // The next resolve reads the server list: the store never answers `ok` for a project it has
      // not seen the server list for this org in.
      expect(await freshStore.resolveScope('o-1', 'p-unread')).toBe('not-found');
      expect(freshIdentity.projectOrgIds).toEqual(['o-1']);
    });
  });

  describe('has', () => {
    it('grants nothing while the session is unknown', () => {
      expect(store.has('run:read')).toBe(false);
    });

    it('grants nothing from an empty set, even for a code the catalogue has', async () => {
      source.granted = new Set<string>();
      await store.bootstrap();

      expect(store.has('run:read')).toBe(false);
      expect(store.has('')).toBe(false);
    });

    it('grants exactly the codes the source loaded', async () => {
      source.granted = new Set(['run:read']);
      await store.bootstrap();

      expect(store.has('run:read')).toBe(true);
      expect(store.has('run:create')).toBe(false);
    });

    it('matches the whole code, never a prefix', async () => {
      source.granted = new Set(['run:read']);
      await store.bootstrap();

      expect(store.has('run')).toBe(false);
      expect(store.has('run:read:x')).toBe(false);
    });
  });
});

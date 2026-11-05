// PolicyStore: `reset` forgets the view — rows, project, head and activation — and abandons a read
// still in flight.

import { TestBed } from '@angular/core/testing';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyDataService } from '../../../core/data/policy-data.service';
import { PolicyProjectReads } from './policy-reads';
import { PolicyStore } from './policy.store';
import {
  ANCHOR_ID,
  FakePolicyDataService,
  FakeSessionStore,
  HEAD_ID,
  page,
  PROJECT_ID,
  revision,
  summary,
} from './policy.store.testing';

describe('PolicyStore (09.7.1-T4)', () => {
  let data: FakePolicyDataService;
  let session: FakeSessionStore;
  let store: PolicyStore;

  beforeEach(() => {
    data = new FakePolicyDataService();
    session = new FakeSessionStore();
    data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
    data.pages = [page([summary(ANCHOR_ID, 2, 'active')], null, HEAD_ID)];
    TestBed.configureTestingModule({
      providers: [
        PolicyStore,
        PolicyProjectReads,
        { provide: PolicyDataService, useValue: data },
        { provide: SessionStore, useValue: session },
      ],
    });
    store = TestBed.inject(PolicyStore);
  });

  describe('reset', () => {
    it('clears the view, returns to idle and abandons a read in flight', async () => {
      data.getManual = true;
      const opening = store.open(ANCHOR_ID);
      store.reset();

      data.settleGet(0, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
      await opening;

      expect(store.phase()).toBe('idle');
      expect(store.projectId()).toBeNull();
      expect(store.revisions()).toEqual([]);
      expect(store.headRevisionId()).toBeNull();
      expect(store.loadError()).toBeNull();
      expect(store.activation()).toBeNull();
    });
  });
});

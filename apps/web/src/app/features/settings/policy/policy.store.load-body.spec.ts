// PolicyStore: revision bodies, cached and shared for the diff and the publish seed, and checked
// against the project the read was asked for rather than the one on screen when it lands.

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
  OTHER_PROJECT_ID,
  page,
  PROJECT_ID,
  revision,
  SECOND_ID,
  summary,
  typedFailure,
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

  describe('loadBody', () => {
    it('reads a body once and serves the cached one after that', async () => {
      data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));

      const first = await store.loadBody(ANCHOR_ID);
      const second = await store.loadBody(ANCHOR_ID);

      expect(second).toBe(first);
      expect(data.getCalls).toEqual([ANCHOR_ID]);
    });

    it('keeps at most eight bodies, dropping the oldest read', async () => {
      for (let index = 0; index < 9; index += 1) {
        const id = `018f0000-0000-7000-8000-0000000000d${index}`;
        data.revisions.set(id, revision(id, PROJECT_ID, index + 1, 'draft'));
        await store.loadBody(id);
      }

      // The ninth read is cached; the first one was dropped, so asking for it reads again.
      const newest = '018f0000-0000-7000-8000-0000000000d8';
      const oldest = '018f0000-0000-7000-8000-0000000000d0';
      const readsBefore = data.getCalls.length;
      await store.loadBody(newest);
      expect(data.getCalls).toHaveLength(readsBefore);
      await store.loadBody(oldest);
      expect(data.getCalls).toHaveLength(readsBefore + 1);
    });

    it('rejects with the server’s failure, which both callers render', async () => {
      data.getFailure = typedFailure(
        {
          error: {
            code: 'not_found',
            message: 'The policy revision was not found.',
            request_id: '018f0000-0000-7000-8000-0000000000e5',
            retryable: false,
          },
        },
        'not_found',
        404,
      );

      await expect(store.loadBody(ANCHOR_ID)).rejects.toMatchObject({
        error: { kind: 'not_found' },
      });
    });

    it('shares one read between concurrent calls for the same revision', async () => {
      // The diff and the publish dialog ask in the same tick; one immutable revision is one GET.
      data.getManual = true;

      const diff = store.loadBody(ANCHOR_ID);
      const dialog = store.loadBody(ANCHOR_ID);
      expect(data.getCalls).toEqual([ANCHOR_ID]);

      const stored = revision(ANCHOR_ID, PROJECT_ID, 2, 'active');
      data.settleGet(0, stored);

      await expect(diff).resolves.toBe(stored);
      await expect(dialog).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID]);
      // The shared answer is the cached one, so a later call costs no request either.
      await expect(store.loadBody(ANCHOR_ID)).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID]);
    });

    it('lets a later call start a new read after the shared one was refused', async () => {
      data.getManual = true;
      const first = store.loadBody(ANCHOR_ID);
      const joined = store.loadBody(ANCHOR_ID);

      const refusal = typedFailure(
        {
          error: {
            code: 'unavailable',
            message: 'The policy store is busy.',
            request_id: '018f0000-0000-7000-8000-0000000000ee',
            retryable: true,
          },
        },
        'unavailable',
        503,
      );
      data.failGet(0, refusal);

      await expect(first).rejects.toBe(refusal);
      await expect(joined).rejects.toBe(refusal);
      // The in-flight entry is gone, so a retry is a real retry rather than the same rejection.
      data.getManual = false;
      const retried = await store.loadBody(ANCHOR_ID);
      expect(retried.id).toBe(ANCHOR_ID);
      expect(data.getCalls).toEqual([ANCHOR_ID, ANCHOR_ID]);
    });

    it('answers its caller but caches nothing when a reset lands mid-read', async () => {
      const stored = revision(ANCHOR_ID, PROJECT_ID, 2, 'active');
      data.revisions.set(ANCHOR_ID, stored);
      data.getManual = true;
      const reading = store.loadBody(ANCHOR_ID);
      store.reset();

      data.settleGet(0, stored);

      await expect(reading).resolves.toBe(stored);
      // The body belonged to a view that is gone, so the cache stays empty and the next call reads.
      data.getManual = false;
      await expect(store.loadBody(ANCHOR_ID)).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID, ANCHOR_ID]);
    });

    it.each([
      ['a bare word', 'current-head'],
      ['a UUID one digit short', '018f0000-0000-7000-8000-00000000000'],
      ['a UUID with a stray character', '018f0000-0000-7000-8000-0000000000zz'],
    ])('requests nothing for %s as the id of a body read', async (_label, id) => {
      // The `412` envelope's head id is server text and reaches this read without passing
      // `open`, so the same shape check applies here — no request, and the state both callers
      // already render for "no such revision".
      await expect(store.loadBody(id)).rejects.toMatchObject({ error: { kind: 'not_found' } });

      expect(data.getCalls).toEqual([]);
      expect(data.pendingGets).toEqual([]);
    });

    it('reads a body of the project on screen', async () => {
      await store.open(ANCHOR_ID);
      const stored = revision(HEAD_ID, PROJECT_ID, 3, 'draft');
      data.revisions.set(HEAD_ID, stored);

      await expect(store.loadBody(HEAD_ID)).resolves.toBe(stored);
      expect(data.getCalls).toEqual([ANCHOR_ID, HEAD_ID]);
    });

    it('refuses another project’s body as not_found and caches nothing', async () => {
      await store.open(ANCHOR_ID);
      // A hostile or buggy `412` may name a head of another project; the body of that revision must
      // never reach the diff or the editor, and the tab must not become a probe for it.
      data.revisions.set(HEAD_ID, revision(HEAD_ID, OTHER_PROJECT_ID, 3, 'draft'));

      await expect(store.loadBody(HEAD_ID)).rejects.toMatchObject({
        error: { kind: 'not_found', code: 'policy_revision_not_found' },
      });

      // Nothing was cached: the next call is a real read again, not the refused answer.
      await expect(store.loadBody(HEAD_ID)).rejects.toMatchObject({ error: { kind: 'not_found' } });
      expect(data.getCalls).toEqual([ANCHOR_ID, HEAD_ID, HEAD_ID]);
    });

    it('answers the project that asked when the page switched project mid-read', async () => {
      // The check compares the answer with the project the READ was made for, never the one on
      // screen when it lands: a page that switched project mid-read still gets the legitimate body
      // of the project it asked from.
      session.rows = [{ id: PROJECT_ID }, { id: OTHER_PROJECT_ID }];
      await store.open(ANCHOR_ID);
      expect(store.projectId()).toBe(PROJECT_ID);

      data.getManual = true;
      const reading = store.loadBody(HEAD_ID);
      const switching = store.open(SECOND_ID);
      data.settleGet(1, revision(SECOND_ID, OTHER_PROJECT_ID, 4, 'active'));
      await switching;
      expect(store.projectId()).toBe(OTHER_PROJECT_ID);

      const stored = revision(HEAD_ID, PROJECT_ID, 3, 'draft');
      data.settleGet(0, stored);

      await expect(reading).resolves.toBe(stored);

      // The body belongs to the view that is gone, so it was not cached: the next call is a real
      // read, and it is refused because the project now on screen is the other one.
      data.getManual = false;
      data.revisions.set(HEAD_ID, stored);
      const readsBefore = data.getCalls.length;
      await expect(store.loadBody(HEAD_ID)).rejects.toMatchObject({ error: { kind: 'not_found' } });
      expect(data.getCalls).toHaveLength(readsBefore + 1);
    });

    it('refuses the on-screen project’s body when another project was the one asked', async () => {
      // The captured project decides, not the arrival-time one: after the switch this body's
      // project IS the one on screen, and it is still not the body this read was asked for.
      session.rows = [{ id: PROJECT_ID }, { id: OTHER_PROJECT_ID }];
      await store.open(ANCHOR_ID);

      data.getManual = true;
      const reading = store.loadBody(HEAD_ID);
      const switching = store.open(SECOND_ID);
      data.settleGet(1, revision(SECOND_ID, OTHER_PROJECT_ID, 4, 'active'));
      await switching;
      expect(store.projectId()).toBe(OTHER_PROJECT_ID);

      data.settleGet(0, revision(HEAD_ID, OTHER_PROJECT_ID, 3, 'draft'));

      await expect(reading).rejects.toMatchObject({
        error: { kind: 'not_found', code: 'policy_revision_not_found' },
      });
    });

    it('answers its caller when a reset lands mid-read of that project’s body', async () => {
      // A reset sends the signal back to `null`, which once SKIPPED the check and
      // hid, behind a passing test, that the check was reading arrival-time state at all.
      await store.open(ANCHOR_ID);
      const stored = revision(HEAD_ID, PROJECT_ID, 3, 'draft');
      data.getManual = true;
      const reading = store.loadBody(HEAD_ID);
      store.reset();
      expect(store.projectId()).toBeNull();

      data.settleGet(0, stored);

      // The captured project still says this answer is the caller's.
      await expect(reading).resolves.toBe(stored);

      // Nothing was cached: the cache was dropped with the view, so the next call reads again.
      data.getManual = false;
      data.revisions.set(HEAD_ID, stored);
      const readsBefore = data.getCalls.length;
      await expect(store.loadBody(HEAD_ID)).resolves.toBe(stored);
      expect(data.getCalls).toHaveLength(readsBefore + 1);
    });

    it('accepts a body of any project when nothing was open when the read was asked', async () => {
      // The documented no-project case: the request carried no project, because there was none to
      // compare against. This is where capture-at-request and the old arrival-time check diverge —
      // a project that OPENS while the body is on its way must not turn a legitimate answer into a
      // refusal. (An unfixed store compares with `OTHER` vs the project now on screen, PROJECT_ID.)
      data.getManual = true;
      const reading = store.loadBody(HEAD_ID);
      const opening = store.open(ANCHOR_ID);
      data.settleGet(1, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
      await opening;
      expect(store.projectId()).toBe(PROJECT_ID);

      const stored = revision(HEAD_ID, OTHER_PROJECT_ID, 3, 'draft');
      data.settleGet(0, stored);

      await expect(reading).resolves.toBe(stored);
    });

    it('refuses a cached body of another project once a project is on screen', async () => {
      // The cache is keyed by revision, not by project: a body read while nothing was open must not
      // become the way around the check once a project is.
      const foreign = revision(HEAD_ID, OTHER_PROJECT_ID, 3, 'draft');
      data.revisions.set(HEAD_ID, foreign);
      // While nothing is open the read is its caller's and the body is cached.
      await expect(store.loadBody(HEAD_ID)).resolves.toBe(foreign);

      await store.open(ANCHOR_ID);
      expect(store.projectId()).toBe(PROJECT_ID);
      const readsBefore = data.getCalls.length;

      await expect(store.loadBody(HEAD_ID)).rejects.toMatchObject({
        error: { kind: 'not_found', code: 'policy_revision_not_found' },
      });
      // Refused from the cache: no new request went out for it.
      expect(data.getCalls).toHaveLength(readsBefore);
    });
  });
});

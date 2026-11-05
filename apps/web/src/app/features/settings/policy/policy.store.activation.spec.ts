// PolicyStore: the activation seam. The mode chip renders exactly `policy_activation.mode`, and
// nothing is derived from a revision's `state`.

import { TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyDataService } from '../../../core/data/policy-data.service';
import { policyModeView } from './policy-mode';
import { PolicyProjectReads } from './policy-reads';
import { PolicyStore } from './policy.store';
import {
  ACTIVATION,
  ANCHOR_ID,
  FakePolicyDataService,
  FakeSessionStore,
  flushMicrotasks,
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

  describe('applyActivation', () => {
    function activation(mode: PolicyActivationStateDto['mode']): PolicyActivationStateDto {
      return {
        project_id: PROJECT_ID,
        policy_revision_id: ANCHOR_ID,
        hash: 'sha256:2',
        mode,
        activated_by: 'u-1',
        activated_at: '2026-10-20T06:05:00Z',
      };
    }

    it.each([
      { label: 'enforcing', mode: 'enforcing' as const, kind: 'gating' },
      { label: 'advisory', mode: 'advisory' as const, kind: 'advisory' },
      // The wire type narrows the mode, but a server-declared third value must never be rounded
      // into one of the two the chip knows: it is shown as unknown with its own text.
      { label: 'an undeclared mode', mode: 'permissive' as unknown as 'advisory', kind: 'unknown' },
    ])('maps $label through applyActivation to the $kind chip', ({ mode, kind }) => {
      store.applyActivation(activation(mode));

      expect(store.mode()).toBe(mode);
      expect(policyModeView(store.mode()).kind).toBe(kind);
    });

    it('keeps the raw undeclared value so the chip can show the server’s own text', () => {
      store.applyActivation(activation('permissive' as unknown as 'advisory'));

      expect(policyModeView(store.mode())).toEqual({ kind: 'unknown', raw: 'permissive' });
    });

    it('answers no mode at all when no activation was applied', () => {
      expect(store.activation()).toBeNull();
      expect(store.mode()).toBeNull();
      expect(policyModeView(store.mode())).toEqual({ kind: 'unavailable' });
    });

    it('replaces an applied activation with the project’s own read on the next open', async () => {
      store.applyActivation(activation('enforcing'));

      await store.open(ANCHOR_ID);
      await flushMicrotasks();

      // The tab never keeps a mode the server did not just confirm for the project on screen.
      expect(store.activation()).toEqual(ACTIVATION);
      expect(store.mode()).toBe('advisory');
    });

    it('clears the applied activation while the project read is still out', async () => {
      let answer: (value: PolicyActivationStateDto) => void = () => undefined;
      data.activation = new Promise<PolicyActivationStateDto>((resolve) => {
        answer = resolve;
      });
      store.applyActivation(activation('enforcing'));

      await store.open(ANCHOR_ID);

      expect(store.activation()).toBeNull();
      expect(policyModeView(store.mode())).toEqual({ kind: 'unavailable' });

      answer(ACTIVATION);
      await flushMicrotasks();
      expect(store.mode()).toBe('advisory');
    });
  });
});

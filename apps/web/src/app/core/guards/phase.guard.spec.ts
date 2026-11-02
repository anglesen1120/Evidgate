// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1; CR-D-2 navigation rule): the phase guard. A later phase's
// route must not match at all — the sidebar hides the entry with the same predicate, and an entry
// that is hidden must also be unreachable by typing its URL.

import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import type { ProductPhase } from '../../shell/nav/nav.models';
import { ACTIVE_PHASE } from '../../shell/nav/product-phase';
import { SessionStore } from '../session/session.store';
import { phaseGuard } from './phase.guard';
import { EMPTY_MATCH_SNAPSHOT } from './spec-helpers';

/** The guard's whole dependency: the idempotent bootstrap every guard awaits. */
class FakeSessionStore {
  bootstrapCalls = 0;

  bootstrap(): Promise<void> {
    this.bootstrapCalls += 1;
    return Promise.resolve();
  }
}

describe('phaseGuard (09.1.1-T4.1)', () => {
  let session: FakeSessionStore;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
  });

  function run(phase: ProductPhase): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      phaseGuard({ id: 'spec-entry', phase })({}, [], EMPTY_MATCH_SNAPSHOT),
    );
  }

  it('matches the active phase', async () => {
    expect(await run(ACTIVE_PHASE)).toBe(true);
  });

  it('matches an earlier phase, so a G1 build keeps serving G1 routes', async () => {
    expect(ACTIVE_PHASE).toBe('G1');
    expect(await run('G1')).toBe(true);
  });

  it('does not match any later phase in a G1 build', async () => {
    for (const phase of ['G2', 'G3', 'G4'] as const) {
      expect(await run(phase)).toBe(false);
    }
  });

  it('waits for the session once before refusing a later phase', async () => {
    await run('G4');

    expect(session.bootstrapCalls).toBe(1);
  });
});

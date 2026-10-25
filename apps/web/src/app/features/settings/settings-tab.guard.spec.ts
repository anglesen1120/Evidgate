// 09.7.1-T1.2 (plan 09.7.1 §2 AC1, §10 T1.2; 00 §9.2; D-S68-Q1/Q4): the per-tab gate. AC1's two
// halves are both here: a tab whose permission the server-declared set lacks does not match, and the
// refusal is a plain `false` (the wildcard route then renders the 404 pattern) rather than a
// `/forbidden` redirect that would confirm the destination exists.
//
// One case per tab in `SETTINGS_TABS`, so a tab added to the strip without a matching gate — or a
// gate that reads a code the registry row does not name — fails here instead of shipping.

import { TestBed } from '@angular/core/testing';
import { UrlTree, provideRouter } from '@angular/router';

import { EMPTY_MATCH_SNAPSHOT } from '../../core/guards/spec-helpers';
import { SessionStore, type SessionState } from '../../core/session/session.store';
import type { ProductPhase } from '../../shell/nav/nav.models';
import { ACTIVE_PHASE } from '../../shell/nav/product-phase';
import { settingsTabGuard } from './settings-tab.guard';
import {
  SETTINGS_TABS,
  isSettingsTabAvailable,
  type SettingsTab,
  type SettingsTabId,
} from './settings-tabs';

/** One tab, labelled for `it.each`, so a failure names the tab and not an index. */
const TAB_CASES = SETTINGS_TABS.map((tab) => ({ id: tab.id, tab }));

/**
 * The phases this build serves, declared here on purpose: an expectation computed with the guard's
 * own `isPhaseActive` would agree with whatever that predicate answered.
 */
const SHIPPED_PHASES: readonly ProductPhase[] = ['G1'];

/** The tab a case names, or a failure naming the missing row. */
function tabOf(id: SettingsTabId): SettingsTab {
  const tab = SETTINGS_TABS.find((candidate) => candidate.id === id);
  if (tab === undefined) {
    throw new Error(`SETTINGS_TABS has no '${id}' tab`);
  }
  return tab;
}

/**
 * The guard's whole dependency: an idempotent bootstrap that loads the set, then the set. `has()`
 * answers `false` until `bootstrap()` settles and unless the session is authenticated, which is what
 * `SessionStore` does — its permission signal stays empty for `unknown`, `anonymous` and
 * `unavailable`, and a decision taken before the load sees nothing.
 */
class FakeSessionStore {
  state: SessionState = 'authenticated';
  granted: ReadonlySet<string> = new Set<string>();
  private loaded = false;

  bootstrap(): Promise<void> {
    return Promise.resolve().then(() => {
      this.loaded = true;
    });
  }

  has(code: string): boolean {
    return this.state === 'authenticated' && this.loaded && this.granted.has(code);
  }
}

describe('isSettingsTabAvailable (09.7.1-T1.1)', () => {
  it.each(TAB_CASES)(
    '$id: available when its phase is reached and the set holds its code',
    ({ tab }) => {
      expect(
        isSettingsTabAvailable(
          tab,
          (code) => code === tab.permission,
          () => true,
        ),
      ).toBe(true);
    },
  );

  it.each(TAB_CASES)('$id: unavailable when the set lacks its code', ({ tab }) => {
    expect(
      isSettingsTabAvailable(
        tab,
        () => false,
        () => true,
      ),
    ).toBe(false);
  });

  it.each(TAB_CASES)(
    '$id: unavailable in a phase the caller has not reached, even with the code',
    ({ tab }) => {
      // An explicit seam: the claim is the phase rule itself, not `isPhaseActive`'s own answer.
      expect(
        isSettingsTabAvailable(
          tab,
          () => true,
          () => false,
        ),
      ).toBe(false);
    },
  );
});

describe('settingsTabGuard (09.7.1-T1.2)', () => {
  let session: FakeSessionStore;

  beforeEach(() => {
    session = new FakeSessionStore();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: SessionStore, useValue: session }],
    });
  });

  /** Runs the guard for one tab, as the router would during a match attempt. */
  async function run(tab: SettingsTab): Promise<unknown> {
    return TestBed.runInInjectionContext(async () =>
      settingsTabGuard(tab)({}, [], EMPTY_MATCH_SNAPSHOT),
    );
  }

  it.each(TAB_CASES)(
    '$id: matches only when this build serves its phase and the set holds its code',
    async ({ tab }) => {
      session.granted = new Set([tab.permission]);

      // A later-phase tab (Secrets G2; Policy, Integrations, Agents G4) stays unmatched even when
      // the session holds its code, so it is as absent as a segment that exists nowhere.
      await expect(run(tab)).resolves.toBe(SHIPPED_PHASES.includes(tab.phase));
    },
  );

  it.each(TAB_CASES)('$id: does not match when the set lacks its code', async ({ tab }) => {
    // Two real codes that no tab of `SETTINGS_TABS` is gated by.
    session.granted = new Set(['organization:read', 'membership:write']);

    await expect(run(tab)).resolves.toBe(false);
  });

  it.each(TAB_CASES)(
    '$id: does not match an unauthenticated session that would hold the code',
    async ({ tab }) => {
      session.state = 'anonymous';
      session.granted = new Set([tab.permission]);

      await expect(run(tab)).resolves.toBe(false);
    },
  );

  it.each(TAB_CASES)('$id: does not match while the session is still unknown', async ({ tab }) => {
    session.state = 'unknown';
    session.granted = new Set([tab.permission]);

    await expect(run(tab)).resolves.toBe(false);
  });

  it('hides the Agents tab in the G1 build even for a caller holding agent_pool:read (CR-D-2)', async () => {
    session.granted = new Set(['agent_pool:read']);

    // Phase hiding is not authorization: the tab is absent, never a disabled teaser.
    expect(ACTIVE_PHASE).toBe('G1');
    await expect(run(tabOf('agents'))).resolves.toBe(false);
  });

  it('refuses with a plain false, never a /forbidden redirect (AC1, 00 §9.2)', async () => {
    const result = await run(tabOf('policy'));

    // A `UrlTree` here would tell the caller the destination exists and they may not use it.
    expect(result).toBe(false);
    expect(result).not.toBeInstanceOf(UrlTree);
  });
});

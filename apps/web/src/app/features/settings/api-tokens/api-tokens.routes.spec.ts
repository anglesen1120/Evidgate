import { TestBed } from '@angular/core/testing';
import { EMPTY_MATCH_SNAPSHOT } from '../../../core/guards/spec-helpers';
import { SessionStore } from '../../../core/session/session.store';
import { ACTIVE_PHASE } from '../../../shell/nav/product-phase';
import { settingsTabGuard } from '../settings-tab.guard';
import {
  availableSettingsTabs,
  isSettingsTabAvailable,
  SETTINGS_TABS,
  type SettingsTab,
} from '../settings-tabs';

function apiTab(): SettingsTab {
  const tab = SETTINGS_TABS.find((row) => row.id === 'api-tokens');
  if (!tab) throw new Error('API tokens tab is not registered');
  return tab;
}

/**
 * Runs the real per-tab guard over a session holding exactly `codes`. The guard reads the phase
 * from the row it is given, and the build serves G1, so a case that wants the permission branch
 * passes the row with its phase declared as reached; the G4 row itself is exercised by the last
 * case.
 */
async function canMatch(tab: SettingsTab, codes: readonly string[]): Promise<boolean> {
  TestBed.configureTestingModule({
    providers: [
      {
        provide: SessionStore,
        useValue: {
          bootstrap: () => Promise.resolve(undefined),
          has: (code: string) => codes.includes(code),
        },
      },
    ],
  });
  return Boolean(
    await TestBed.runInInjectionContext(() => settingsTabGuard(tab)({}, [], EMPTY_MATCH_SNAPSHOT)),
  );
}

describe('API tokens tab access', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('is absent for a member without api_token:read even when G4 is active', () => {
    expect(
      availableSettingsTabs(
        (code) => code === 'project:read',
        () => true,
      ).map((tab) => tab.id),
    ).not.toContain('api-tokens');
    expect(
      isSettingsTabAvailable(
        apiTab(),
        (code) => code === 'api_token:read',
        () => true,
      ),
    ).toBe(true);
  });

  it('remains absent before its G4 phase even with read permission', () => {
    expect(apiTab().phase).toBe('G4');
    expect(
      isSettingsTabAvailable(
        apiTab(),
        () => true,
        () => false,
      ),
    ).toBe(false);
  });

  it('matches direct navigation with api_token:read once the phase is reached', async () => {
    // `ACTIVE_PHASE` is what the guard's own `isPhaseActive` answers for a reached phase, so the
    // row below differs from the registry only in the phase the build would have to serve.
    const reached: SettingsTab = { ...apiTab(), phase: ACTIVE_PHASE };

    await expect(canMatch(reached, ['api_token:read'])).resolves.toBe(true);
  });

  it('refuses direct navigation without api_token:read once the phase is reached', async () => {
    const reached: SettingsTab = { ...apiTab(), phase: ACTIVE_PHASE };

    await expect(canMatch(reached, ['project:read'])).resolves.toBe(false);
  });

  it('refuses the G4 tab in the G1 build even when the grant is held', async () => {
    // Phase hiding is not authorization: the row is refused before its permission code is read.
    await expect(canMatch(apiTab(), ['api_token:read'])).resolves.toBe(false);
  });
});

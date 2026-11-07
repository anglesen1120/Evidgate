import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EMPTY_MATCH_SNAPSHOT } from '../../../core/guards/spec-helpers';
import { SessionStore } from '../../../core/session/session.store';
import { settingsTabGuard } from '../settings-tab.guard';
import { availableSettingsTabs, isSettingsTabAvailable, SETTINGS_TABS } from '../settings-tabs';

function apiTab() {
  const tab = SETTINGS_TABS.find((row) => row.id === 'api-tokens');
  if (!tab) throw new Error('API tokens tab is not registered');
  return tab;
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

  it('returns false from CanMatch for direct permissionless navigation', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionStore,
          useValue: { bootstrap: () => Promise.resolve(undefined), has: () => false },
        },
      ],
    });
    const result = TestBed.runInInjectionContext(() =>
      settingsTabGuard(apiTab())({}, [], EMPTY_MATCH_SNAPSHOT),
    );
    await expect(result).resolves.toBe(false);
  });
});

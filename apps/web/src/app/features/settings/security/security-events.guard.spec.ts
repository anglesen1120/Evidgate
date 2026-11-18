import { TestBed } from '@angular/core/testing';
import type { CanMatchFn } from '@angular/router';
import { SessionStore } from '../../../core/session/session.store';
import { EMPTY_MATCH_SNAPSHOT } from '../../../core/guards/spec-helpers';
import { settingsTabGuard } from '../settings-tab.guard';
import { SETTINGS_TABS, availableSettingsTabs, type SettingsTab } from '../settings-tabs';
import { routes } from '../settings-shell.routes';
import { SecurityEventsPage } from './security-events.page';

const tab = SETTINGS_TABS.find((value) => value.id === 'security-events');
if (!tab) throw new Error('Security events tab must exist');
const securityTab: SettingsTab = tab;

describe('Security events tab authorization', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([false, true])(
    'matches only with its own permission when phase is open: %s',
    async (allowed) => {
      TestBed.configureTestingModule({
        providers: [
          {
            provide: SessionStore,
            useValue: {
              bootstrap: () => Promise.resolve(),
              has: (permission: string) => allowed && permission === securityTab.permission,
            },
          },
        ],
      });
      const guard: CanMatchFn = settingsTabGuard({ ...securityTab, phase: 'G1' });
      const result = await TestBed.runInInjectionContext(() => guard({}, [], EMPTY_MATCH_SNAPSHOT));
      expect(result).toBe(allowed);
      expect(
        availableSettingsTabs(
          (code) => allowed && code === securityTab.permission,
          () => true,
        ).some((value) => value.id === 'security-events'),
      ).toBe(allowed);
    },
  );
  it('wires the permission-gated real view, not a placeholder', () => {
    const route = routes[0]?.children?.find((value) => value.path === 'security-events');
    expect(route?.component).toBe(SecurityEventsPage);
    expect(route?.canMatch).toHaveLength(1);
    expect(securityTab.permission).toBe('security_event:read');
    expect(securityTab.phase).toBe('G2');
  });
});

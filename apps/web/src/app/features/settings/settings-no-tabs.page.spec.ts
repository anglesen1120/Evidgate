import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { SettingsNoTabsPage } from './settings-no-tabs.page';
import { SETTINGS_TABS } from './settings-tabs';

describe('SettingsNoTabsPage', () => {
  it('renders the shared empty pattern and names no destination from the registry', async () => {
    TestBed.configureTestingModule({
      imports: [SettingsNoTabsPage],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(SettingsNoTabsPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('app-empty-pattern .state-panel--empty')).not.toBeNull();
    expect(root.classList.contains('min-w-0')).toBe(true);
    // No way out of the empty state: no destination element, link or control.
    expect(root.querySelector('button, a, [data-settings-tab]')).toBeNull();
    for (const tab of SETTINGS_TABS) {
      expect(root.textContent).not.toContain(tab.label);
      expect(root.querySelector(`[data-settings-tab="${tab.id}"]`)).toBeNull();
    }
  });
});

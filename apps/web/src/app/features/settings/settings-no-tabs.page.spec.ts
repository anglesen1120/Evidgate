// 09.7.1-T1.1/T1.2: reuse the empty pattern without disclosing unavailable destinations.
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { SettingsNoTabsPage } from './settings-no-tabs.page';
import { SETTINGS_TABS } from './settings-tabs';

describe('SettingsNoTabsPage', () => {
  it('renders a shared empty pattern with administrator guidance and no destination names', async () => {
    TestBed.configureTestingModule({
      imports: [SettingsNoTabsPage],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(SettingsNoTabsPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('app-empty-pattern .state-panel--empty')).not.toBeNull();
    expect(root.querySelector('h2')?.textContent).toBe('No settings destinations');
    expect(root.textContent).toContain('Ask an administrator.');
    expect(root.querySelector('button, a, [data-settings-tab]')).toBeNull();
    for (const tab of SETTINGS_TABS) {
      expect(root.textContent).not.toContain(tab.label);
    }
  });
});

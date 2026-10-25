// 09.7.1-T1.1 (D-S68-Q3): tab identity comes only from route data and the shared registry.
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';

import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';
import { SETTINGS_TABS } from './settings-tabs';

describe('SettingsTabPlaceholderPage', () => {
  for (const tab of SETTINGS_TABS) {
    it(`renders ${tab.id} from route data with its registry label and host hook`, async () => {
      TestBed.configureTestingModule({
        imports: [SettingsTabPlaceholderPage],
        providers: [
          provideZonelessChangeDetection(),
          { provide: ActivatedRoute, useValue: { snapshot: { data: { settingsTabId: tab.id } } } },
        ],
      });
      const fixture = TestBed.createComponent(SettingsTabPlaceholderPage);
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.getAttribute('data-settings-tab-body')).toBe(tab.id);
      expect(root.querySelector('h2')?.textContent).toBe(tab.label);
      expect(root.textContent).toContain('This tab arrives with its own feature story.');
      expect(root.querySelector('input, button')).toBeNull();
    });
  }

  it('does not invent a destination for unknown route data', async () => {
    TestBed.configureTestingModule({
      imports: [SettingsTabPlaceholderPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ActivatedRoute, useValue: { snapshot: { data: { settingsTabId: 'unknown' } } } },
      ],
    });
    const fixture = TestBed.createComponent(SettingsTabPlaceholderPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.hasAttribute('data-settings-tab-body')).toBe(false);
    expect(root.querySelector('h2')).toBeNull();
  });
});

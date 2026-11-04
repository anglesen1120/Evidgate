import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';

import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';

/** Renders the page with `settingsTabId` supplied the way the route table supplies it. */
async function render(settingsTabId: string): Promise<HTMLElement> {
  TestBed.configureTestingModule({
    imports: [SettingsTabPlaceholderPage],
    providers: [
      provideZonelessChangeDetection(),
      { provide: ActivatedRoute, useValue: { snapshot: { data: { settingsTabId } } } },
    ],
  });
  const fixture = TestBed.createComponent(SettingsTabPlaceholderPage);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('SettingsTabPlaceholderPage', () => {
  it('takes the destination id and the heading from the route data', async () => {
    const root = await render('secrets');
    expect(root.getAttribute('data-settings-tab-body')).toBe('secrets');
    expect(root.querySelector('h2[hlmCardTitle]')?.textContent?.trim()).toBeTruthy();
    expect(root.querySelector('section[hlmCard]')).not.toBeNull();
    expect(root.querySelector('[hlmCardDescription]')?.textContent).toContain('This tab arrives');
    // A body hook without a resource behind it: no control invites an action that cannot work.
    expect(root.querySelector('input, button')).toBeNull();
  });

  it('does not invent a destination for unknown route data', async () => {
    const root = await render('unknown');
    expect(root.hasAttribute('data-settings-tab-body')).toBe(false);
    expect(root.querySelector('h2')).toBeNull();
  });
});

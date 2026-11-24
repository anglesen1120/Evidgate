import type { ComponentFixture } from '@angular/core/testing';

import type { OnboardingShellComponent } from './onboarding-shell.component';
import { mountShell, type ShellStore } from './onboarding-shell.spec-support';

describe('OnboardingShellComponent loading announcements', () => {
  let fixture: ComponentFixture<OnboardingShellComponent>;
  let root: HTMLElement;
  let store: ShellStore;

  async function expectSetupAnnouncement() {
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(root.querySelector('app-loading-pattern [role="status"]')?.textContent?.trim()).toBe(
        'Loading project setup',
      );
    });
  }

  beforeEach(async () => {
    ({ fixture, root, store } = await mountShell());
  });

  it('names the pending setup read instead of evidence while retaining its accessible busy state', async () => {
    store.status.set('loading');
    await fixture.whenStable();
    const loading = root.querySelector('app-loading-pattern [role="status"]');
    expect(loading).not.toBeNull();
    expect(loading?.getAttribute('aria-live')).toBe('polite');
    expect(root.querySelector('app-loading-pattern [aria-busy="true"]')).not.toBeNull();
    await expectSetupAnnouncement();
    expect(loading?.textContent).not.toContain('Loading evidence');
  });

  it('removes the loading announcement when ready and uses setup copy again on the next read', async () => {
    for (let read = 0; read < 2; read += 1) {
      expect(root.querySelector('app-loading-pattern [data-table-progress]')).toBeNull();
      store.status.set('loading');
      await fixture.whenStable();
      expect(root.querySelectorAll('app-loading-pattern [role="status"]')).toHaveLength(1);
      await expectSetupAnnouncement();
      store.status.set('ready');
      await fixture.whenStable();
      expect(root.querySelector('app-loading-pattern [data-table-progress]')).toBeNull();
    }
  });

  it('uses setup copy after a failed read without retaining error recovery in the busy region', async () => {
    store.failure.set('unavailable');
    store.status.set('error');
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.querySelector('app-loading-pattern [data-table-progress]')).toBeNull();
    store.status.set('loading');
    await fixture.whenStable();
    expect(root.querySelector('app-error-pattern')).toBeNull();
    await expectSetupAnnouncement();
  });
});

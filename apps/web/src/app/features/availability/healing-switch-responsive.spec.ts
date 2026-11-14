import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { stubScopeBrowserApis } from '../../shell/layout/scope.fixture';
import { mount } from './healing-switch-page.fixture';
import { TRIAGE, resolution, switchRow } from './healing-switch.test-support';
import { SwitchScopeControl } from './switch-scope-control.component';

describe('Availability page composition and narrow policy', () => {
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  it('orders effective state, scope hierarchy and separate configuration before dependency detail', async () => {
    const { root, settle } = mount();
    await settle();
    expect([...root.querySelectorAll('h2[id]')].map((heading) => heading.id)).toEqual([
      'healing-switch-posture',
      'healing-switch-hierarchy',
      'healing-switch-configuration',
      'healing-switch-health',
    ]);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
  });

  it('keeps every scope fact and dependency visible but removes narrow mutation controls', async () => {
    const { fixture, root, desktop, data, settle } = mount({
      answer: resolution({
        project: switchRow('project', 'enabled', { reason: 'A complete scope reason' }),
      }),
    });
    await settle();
    desktop.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('button[role="combobox"], textarea, [data-healing-save]')).toBeNull();
    expect(root.textContent).toContain('Desktop required');
    expect(root.querySelectorAll('app-desktop-required-notice')).toHaveLength(1);
    expect(root.textContent).toContain('A complete scope reason');
    expect(root.querySelectorAll('[data-healing-scope]')).toHaveLength(3);
    expect(root.querySelectorAll('[data-healing-health]')).toHaveLength(2);
    expect(data.writes).toHaveLength(0);
  });

  it('guards the page submit boundary too when a stale child output arrives during resize', async () => {
    const { fixture, desktop, data, settle } = mount();
    await settle();
    const control = fixture.debugElement
      .query(By.directive(SwitchScopeControl))
      .injector.get(SwitchScopeControl);
    desktop.set(false);
    control.submitted.emit({ mode: 'disabled', reason: null });
    await settle();
    expect(data.writes).toHaveLength(0);
    desktop.set(true);
    await settle();
    expect(data.writes).toHaveLength(0);
  });

  it.each([{ codes: [] }, { codes: [TRIAGE] }])(
    'never grants configuration or leaks a desktop action to $codes',
    async ({ codes }) => {
      const { fixture, root, desktop, data, settle } = mount({ codes });
      await settle();
      desktop.set(false);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(root.querySelector('[data-healing-save]')).toBeNull();
      expect(root.textContent).not.toContain('Desktop required');
      expect(data.writes).toHaveLength(0);
      if (codes.length === 0) {
        expect(root.querySelector('[data-healing-scope]')).toBeNull();
        expect(data.reads).toHaveLength(0);
      }
    },
  );
});

import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { stubScopeBrowserApis } from '../../shell/layout/scope.fixture';
import { mount } from './healing-switch-page.fixture';
import { TRIAGE, resolution, switchRow } from './healing-switch.test-support';
import { SwitchScopeControl } from './switch-scope-control.component';

class AvailabilityHarness extends ComponentHarness {
  static hostSelector = 'app-healing-switch-page';
  readonly projectMode = this.locatorFor('[data-snapshot-project-mode]');
  readonly decidingActor = this.locatorForOptional('[data-snapshot-actor]');
  readonly hierarchyResult = this.locatorFor('[data-healing-hierarchy-result]');
  readonly storedSetting = this.locatorFor(
    '[data-healing-editor="project"] [data-healing-stored-mode]',
  );
}

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

  it('pairs effective state with runtime context before hierarchy, configuration and dependencies', async () => {
    const { root, settle } = mount();
    await settle();
    expect([...root.querySelectorAll('h2[id]')].map((heading) => heading.id)).toEqual([
      'healing-switch-posture',
      'healing-switch-runtime',
      'healing-switch-hierarchy',
      'healing-switch-configuration',
      'healing-switch-health',
    ]);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
  });

  it('distinguishes the stored project mode from the deciding scope and its actor', async () => {
    const { fixture, settle } = mount({
      answer: resolution({
        resolved: 'disabled',
        tenant: switchRow('tenant', 'disabled', { set_by: 'Tenant administrator' }),
        project: switchRow('project', 'enabled', { set_by: 'Project maintainer' }),
      }),
    });
    await settle();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, AvailabilityHarness);
    expect(await (await page.hierarchyResult()).text()).toBe('Disabled');
    expect(await (await page.projectMode()).text()).toBe('Enabled');
    expect(await (await page.decidingActor())?.text()).toBe('Tenant administrator');
    expect(await (await page.storedSetting()).text()).toBe('Enabled');
  });

  it('keeps a redacted system actor hidden and does not invent a stored project mode', async () => {
    const { fixture, settle } = mount({
      answer: resolution({
        resolved: 'disabled',
        system: switchRow('system', 'disabled', { set_by: null }),
      }),
    });
    await settle();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, AvailabilityHarness);
    expect(await (await page.projectMode()).text()).toBe('Not recorded');
    expect(await (await page.decidingActor())?.text()).toBe('Hidden');
    expect(await (await page.storedSetting()).text()).toBe('Not recorded');
  });

  it('does not attribute a dependency outage to the actor of a stored switch', async () => {
    const { fixture, settle } = mount({
      answer: resolution({
        resolved: 'unavailable',
        project: switchRow('project', 'enabled', { set_by: 'Project maintainer' }),
      }),
    });
    await settle();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, AvailabilityHarness);
    expect(await (await page.hierarchyResult()).text()).toBe('Unavailable');
    expect(await (await page.projectMode()).text()).toBe('Enabled');
    expect(await page.decidingActor()).toBeNull();
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

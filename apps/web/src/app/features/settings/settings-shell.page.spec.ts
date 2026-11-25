import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { SessionStore } from '../../core/session/session.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { SettingsShellPage } from './settings-shell.page';
import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';
import { SETTINGS_TABS } from './settings-tabs';

const permissions = signal<ReadonlySet<string>>(new Set());
const currentOrg = signal<{ id: string; name: string } | null>({
  id: 'o',
  name: 'Example organization',
});
const currentProject = signal<{ id: string } | null>(null);

function tabIds(root: HTMLElement): (string | null)[] {
  return Array.from(root.querySelectorAll('[data-settings-tab]'), (link) =>
    link.getAttribute('data-settings-tab'),
  );
}

async function render(): Promise<HTMLElement> {
  const fixture = TestBed.createComponent(SettingsShellPage);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}
describe('SettingsShellPage', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    permissions.set(new Set());
    currentOrg.set({ id: 'o', name: 'Example organization' });
    currentProject.set(null);
    TestBed.configureTestingModule({
      imports: [SettingsShellPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          {
            path: 'orgs/:orgId/settings',
            component: SettingsShellPage,
            children: SETTINGS_TABS.map((tab) => ({
              path: tab.segment,
              component: SettingsTabPlaceholderPage,
              data: { settingsTabId: tab.id },
            })),
          },
        ]),
        {
          provide: SessionStore,
          useValue: {
            permissions: permissions.asReadonly(),
            currentOrg: currentOrg.asReadonly(),
            currentProject: currentProject.asReadonly(),
            has: (code: string) => permissions().has(code),
          },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it('renders only Audit and no forbidden destination element or label', async () => {
    permissions.set(new Set(['audit:read']));
    const root = await render();
    expect(tabIds(root)).toEqual(['audit']);
    expect(root.querySelectorAll('nav a')).toHaveLength(1);
    expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe('Settings destinations');
    for (const tab of SETTINGS_TABS.filter((tab) => tab.id !== 'audit')) {
      expect(root.querySelector(`[data-settings-tab="${tab.id}"]`)).toBeNull();
      expect(root.textContent).not.toContain(tab.label);
    }
    // Route-linked destinations are links with aria-current, not ARIA tabs, in one scrolling row.
    expect(root.querySelector('[role="tablist"], [role="tab"]')).toBeNull();
    expect(root.querySelector('hlm-select, select')).toBeNull();
    expect(root.querySelector('nav')?.classList.contains('overflow-x-auto')).toBe(true);
    expect(root.querySelector('nav ul')?.classList.contains('flex-nowrap')).toBe(true);
    expect(root.querySelector('.settings-content[hlmCard]')).toBeNull();
    expect(root.querySelector('[hidden]')).toBeNull();
    expect(root.querySelectorAll('[data-settings-group]')).toHaveLength(1);
    expect(root.querySelector('[data-settings-group="billing"]')?.textContent).toContain(
      'Billing & Audit',
    );
  });

  it('preserves Members, Quota & retention, Audit order in the tab links', async () => {
    permissions.set(new Set(['membership:read', 'audit:read', 'usage:read']));
    const root = await render();
    expect(tabIds(root)).toEqual(['members', 'quota-retention', 'audit']);
    expect(root.querySelectorAll('[role="tab"]')).toHaveLength(0);
  });

  it('never exposes Agents in G1 even with agent_pool:read', async () => {
    permissions.set(new Set(['agent_pool:read']));
    const root = await render();
    expect(root.querySelector('[data-settings-tab="agents"]')).toBeNull();
    expect(root.textContent).not.toContain('Agents');
    expect(root.querySelector('nav')).toBeNull();
  });

  it('reacts to server permission additions and revocations', async () => {
    permissions.set(new Set(['audit:read']));
    const fixture = TestBed.createComponent(SettingsShellPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(tabIds(root)).toEqual(['audit']);
    permissions.set(new Set(['membership:read', 'usage:read']));
    await fixture.whenStable();
    expect(tabIds(root)).toEqual(['members', 'quota-retention']);
    expect(root.querySelector('[data-settings-tab="audit"]')).toBeNull();
    permissions.set(new Set());
    await fixture.whenStable();
    expect(root.querySelector('nav')).toBeNull();
  });

  it('groups the visible tabs under their sections in section order', async () => {
    permissions.set(new Set(['membership:read', 'usage:read', 'audit:read']));
    const root = await render();
    const sections = Array.from(root.querySelectorAll('[data-settings-group]'), (el) =>
      el.getAttribute('data-settings-group'),
    );
    expect(sections).toEqual(['access', 'billing']);
    expect(tabIds(root.querySelector('[data-settings-group="access"]') as HTMLElement)).toEqual([
      'members',
    ]);
    expect(tabIds(root.querySelector('[data-settings-group="billing"]') as HTMLElement)).toEqual([
      'quota-retention',
      'audit',
    ]);
  });

  it('offers project AI settings in Workspace only with its permission and a selected project', async () => {
    permissions.set(new Set(['authoring:read']));
    const fixture = TestBed.createComponent(SettingsShellPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const selector = '[data-settings-group="workspace"] a[data-settings-tab="ai-settings"]';
    expect(root.querySelector(selector)).toBeNull();
    currentProject.set({ id: 'p' });
    await fixture.whenStable();
    expect(root.querySelector(selector)?.getAttribute('href')).toBe(
      '/orgs/o/projects/p/ai-settings',
    );
    expect(root.querySelector(selector)?.hasAttribute('aria-current')).toBe(false);
    permissions.set(new Set());
    await fixture.whenStable();
    expect(root.querySelector(selector)).toBeNull();
  });

  it('renders the current organization as text without inventing a missing scope', async () => {
    currentOrg.set({ id: 'o', name: '<img src=x onerror=alert(1)>' });
    const fixture = TestBed.createComponent(SettingsShellPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(root.querySelector('img')).toBeNull();
    currentOrg.set(null);
    await fixture.whenStable();
    expect(root.querySelector('.settings-scope')).toBeNull();
  });

  it('marks the routed link current and follows browser history', async () => {
    permissions.set(new Set(['membership:read', 'audit:read']));
    const harness = await RouterTestingHarness.create('/orgs/o/settings/audit');
    const root = harness.routeNativeElement!;
    expect(root.querySelector('[data-settings-tab="audit"]')?.getAttribute('aria-current')).toBe(
      'page',
    );
    expect(root.querySelector('[data-settings-tab="members"]')?.hasAttribute('aria-current')).toBe(
      false,
    );
    root.querySelector<HTMLAnchorElement>('[data-settings-tab="members"]')!.click();
    await harness.fixture.whenStable();
    expect(TestBed.inject(Router).url).toBe('/orgs/o/settings/members');
    expect(root.querySelector('[data-settings-tab="members"]')?.getAttribute('aria-current')).toBe(
      'page',
    );
    await harness.navigateByUrl('/orgs/o/settings/audit');
    expect(root.querySelector('[data-settings-tab="audit"]')?.getAttribute('aria-current')).toBe(
      'page',
    );
  });

  it('shows the active destination permission only while the server still grants it', async () => {
    permissions.set(new Set(['audit:read', 'membership:read']));
    const harness = await RouterTestingHarness.create('/orgs/o/settings/audit');
    const root = harness.routeNativeElement!;
    expect(root.querySelector('.settings-scope code')?.textContent?.trim()).toBe('audit:read');
    permissions.set(new Set(['membership:read']));
    harness.fixture.detectChanges();
    expect(root.querySelector('.settings-scope code')).toBeNull();
  });
});

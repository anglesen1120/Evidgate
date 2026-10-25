// 09.7.1-T1.1/T1.2 (AC1): unavailable destinations are absent, not disabled or hidden.
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { SessionStore } from '../../core/session/session.store';
import { SettingsShellPage } from './settings-shell.page';
import { SettingsTabPlaceholderPage } from './settings-tab-placeholder.page';
import { SETTINGS_TABS } from './settings-tabs';

const permissions = signal<ReadonlySet<string>>(new Set());
const currentOrg = signal<{ name: string } | null>({ name: 'Example organization' });

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
  beforeEach(() => {
    permissions.set(new Set());
    currentOrg.set({ name: 'Example organization' });
    TestBed.configureTestingModule({
      imports: [SettingsShellPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          {
            path: 'settings',
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
            has: (code: string) => permissions().has(code),
          },
        },
      ],
    });
  });

  it('renders only Audit and no forbidden destination element, label or option', async () => {
    permissions.set(new Set(['audit:read']));
    const root = await render();
    expect(tabIds(root)).toEqual(['audit']);
    expect(root.querySelectorAll('a')).toHaveLength(1);
    expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe('Settings destinations');
    for (const tab of SETTINGS_TABS.filter((tab) => tab.id !== 'audit')) {
      expect(root.querySelector(`[data-settings-tab="${tab.id}"]`)).toBeNull();
      expect(root.textContent).not.toContain(tab.label);
      expect(root.querySelector(`option[value="${tab.segment}"]`)).toBeNull();
      expect(root.querySelector('select')?.textContent).not.toContain(tab.label);
    }
    expect(root.querySelector('[role="tablist"]')).toBeNull();
    expect(root.querySelector('[hidden]')).toBeNull();
  });

  it('preserves Members, Quota & retention, Audit order in links and mobile choices', async () => {
    permissions.set(new Set(['membership:read', 'audit:read', 'project:read']));
    const root = await render();
    const expected = ['members', 'quota-retention', 'audit'];
    expect(tabIds(root)).toEqual(expected);
    expect(
      Array.from(root.querySelectorAll('option:not([disabled])'), (option) =>
        option.getAttribute('value'),
      ),
    ).toEqual(expected);
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
    permissions.set(new Set(['membership:read', 'project:read']));
    await fixture.whenStable();
    expect(tabIds(root)).toEqual(['members', 'quota-retention']);
    expect(root.querySelector('option[value="audit"]')).toBeNull();
    permissions.set(new Set());
    await fixture.whenStable();
    expect(root.querySelector('nav')).toBeNull();
  });

  it('renders the current organization as text without inventing a missing scope', async () => {
    currentOrg.set({ name: '<img src=x onerror=alert(1)>' });
    const fixture = TestBed.createComponent(SettingsShellPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Organization: <img src=x onerror=alert(1)>');
    expect(root.querySelector('img')).toBeNull();
    currentOrg.set(null);
    await fixture.whenStable();
    expect(root.querySelector('.settings-header p')).toBeNull();
  });

  it('marks the routed link current and synchronizes mobile navigation and browser history', async () => {
    permissions.set(new Set(['membership:read', 'audit:read']));
    const harness = await RouterTestingHarness.create('/settings/audit');
    const root = harness.routeNativeElement!;
    expect(root.querySelector('[data-settings-tab="audit"]')?.getAttribute('aria-current')).toBe(
      'page',
    );
    expect(root.querySelector('[data-settings-tab="members"]')?.hasAttribute('aria-current')).toBe(
      false,
    );
    const select = root.querySelector('select')!;
    expect(select.value).toBe('audit');
    select.value = 'members';
    select.dispatchEvent(new Event('change'));
    await harness.fixture.whenStable();
    expect(TestBed.inject(Router).url).toBe('/settings/members');
    await harness.navigateByUrl('/settings/audit');
    expect(select.value).toBe('audit');
  });
});

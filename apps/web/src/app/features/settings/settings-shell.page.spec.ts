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

/** Segments whose child route refuses to match, so the router aborts that navigation. */
const refusedSegments = new Set<string>();

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
    refusedSegments.clear();
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
              canMatch: [() => !refusedSegments.has(tab.segment)],
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
    expect(root.querySelector('a[hlmBtn]')).not.toBeNull();
    expect(root.querySelector('label[hlmLabel]')?.getAttribute('for')).toBe('settings-destination');
    expect(root.querySelector('select[hlmInput]')?.id).toBe('settings-destination');
    expect(root.querySelector('.settings-mobile')?.classList.contains('md:hidden')).toBe(true);
    expect(root.querySelector('[hidden]')).toBeNull();
  });

  it('preserves Members, Quota & retention, Audit order in links and mobile choices', async () => {
    permissions.set(new Set(['membership:read', 'audit:read', 'usage:read']));
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
    permissions.set(new Set(['membership:read', 'usage:read']));
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

  it('keeps the mobile select on the router segment when the navigation is refused', async () => {
    permissions.set(new Set(['membership:read', 'audit:read']));
    refusedSegments.add('audit');
    const harness = await RouterTestingHarness.create('/settings/members');
    const root = harness.routeNativeElement!;
    const select = root.querySelector('select')!;
    expect(select.value).toBe('members');

    // The router refuses this pick and the URL never moves, so the control may not keep showing it.
    select.value = 'audit';
    select.dispatchEvent(new Event('change'));
    // An errored navigation renders nothing and registers no pending task: wait for the control.
    await vi.waitFor(() => expect(select.value).toBe('members'));

    expect(TestBed.inject(Router).url).toBe('/settings/members');
    expect(select.selectedOptions[0]?.getAttribute('value')).toBe('members');
  });

  it('keeps the mobile select on the URL segment when the pick stopped being a destination', async () => {
    permissions.set(new Set(['membership:read', 'audit:read']));
    const harness = await RouterTestingHarness.create('/settings/audit');
    const root = harness.routeNativeElement!;
    const select = root.querySelector('select')!;
    expect(select.value).toBe('audit');

    // Revoked without change detection, so `members` is still an `<option>` while `tabs()` no
    // longer offers it: a pick can arrive for a destination that stopped existing a moment ago.
    permissions.set(new Set(['audit:read']));
    select.value = 'members';
    select.dispatchEvent(new Event('change'));

    // Refused before any navigation starts, so the control returns to the URL in the same turn.
    expect(TestBed.inject(Router).url).toBe('/settings/audit');
    expect(select.value).toBe('audit');
    await harness.fixture.whenStable();
    expect(select.value).toBe('audit');
  });
});

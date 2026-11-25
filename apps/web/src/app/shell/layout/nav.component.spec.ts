import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { NotFoundPage } from '../pages/not-found.page';
import { NavComponent } from './nav.component';
import type { NavGroupView, NavSettingsItem } from '../nav/nav.models';
import { stubScopeBrowserApis } from './scope.fixture';

class NavigationHarness extends ComponentHarness {
  static hostSelector = 'app-shell-nav';
  readonly landmark = this.locatorFor('nav');
  readonly regions = this.locatorForAll('section, [role="region"]');
  readonly groups = this.locatorForAll('[role="group"]');
  readonly heading = this.locatorFor('h2');
  readonly links = this.locatorForAll('a');
}

const groups: readonly NavGroupView[] = [
  {
    id: 'workspace',
    label: 'Workspace',
    items: [{ id: 'overview', label: 'Overview', icon: 'home', link: ['/overview'] }],
  },
  { id: 'administration', label: 'Administration', items: [] },
];

describe('NavComponent', () => {
  let attachedRoot: HTMLElement | null = null;
  let restoreBrowserApis: () => void;

  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
  });

  afterEach(() => {
    attachedRoot?.remove();
    attachedRoot = null;
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [NavComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: NotFoundPage }]),
      ],
    }),
  );

  it('renders only provided nonempty groups in a named navigation landmark', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    await fixture.whenStable();
    const nav = await TestbedHarnessEnvironment.harnessForFixture(fixture, NavigationHarness);
    expect(await (await nav.landmark()).getAttribute('aria-label')).toBe('Primary navigation');
    expect(await nav.regions()).toHaveLength(0);
    const visibleGroups = await nav.groups();
    expect(visibleGroups).toHaveLength(1);
    const heading = await nav.heading();
    expect(await visibleGroups[0]?.getAttribute('aria-labelledby')).toBe(
      await heading.getAttribute('id'),
    );
    expect(await heading.text()).toBe(groups[0]?.label);
    expect(await nav.links()).toHaveLength(1);
  });

  it('marks the active link as the current page and emits link selection', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    const selected = vi.fn();
    fixture.componentInstance.itemSelected.subscribe(selected);
    await fixture.whenStable();
    await TestBed.inject(Router).navigateByUrl('/overview');
    await fixture.whenStable();
    const link = (fixture.nativeElement as HTMLElement).querySelector('a')!;
    expect(link.getAttribute('aria-current')).toBe('page');
    expect(link.hasAttribute('hlmBtn')).toBe(true);
    link.click();
    expect(selected).toHaveBeenCalledExactlyOnceWith(['/overview']);
  });

  it('retains accessible names in rail mode and renders no invented items for an empty view', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    fixture.componentRef.setInput('collapsed', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.classList.contains('rail')).toBe(true);
    expect(root.querySelector('a')?.getAttribute('aria-label')).toBe('Overview');
    expect(root.getAttribute('data-collapsed')).toBe('true');
    expect(root.querySelector('h2 [data-slot="badge"]')).toBeNull();
    expect(root.querySelector('a')?.hasAttribute('title')).toBe(false);
    fixture.componentRef.setInput('groups', []);
    await fixture.whenStable();
    expect(root.querySelectorAll('a')).toHaveLength(0);
  });

  it.each(['hover', 'focus'])(
    'shows the full rail label on %s and dismisses it with Escape',
    async (trigger) => {
      const fixture = TestBed.createComponent(NavComponent);
      fixture.componentRef.setInput('groups', groups);
      fixture.componentRef.setInput('collapsed', true);
      attachedRoot = fixture.nativeElement as HTMLElement;
      document.body.append(attachedRoot);
      await fixture.whenStable();
      const link = attachedRoot.querySelector<HTMLAnchorElement>('a')!;
      if (trigger === 'focus') link.focus();
      else {
        const event = new Event('pointerenter');
        Object.defineProperty(event, 'pointerType', { value: 'mouse' });
        link.dispatchEvent(event);
      }

      await vi.waitFor(() => {
        expect(document.querySelector('[role="tooltip"]')?.textContent?.trim()).toBe('Overview');
      });
      expect(link.getAttribute('aria-label')).toBe('Overview');
      expect(
        document.querySelector('[role="tooltip"] span')?.getAttribute('aria-hidden'),
      ).toBeNull();
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await vi.waitFor(() => expect(document.querySelector('[role="tooltip"]')).toBeNull());
      if (trigger === 'focus') expect(document.activeElement).toBe(link);
    },
  );

  it.each(['hover', 'focus'])(
    'shows a short visible label while retaining full context on %s',
    async (trigger) => {
      const fixture = TestBed.createComponent(NavComponent);
      fixture.componentRef.setInput('groups', [
        {
          id: 'administration',
          label: 'Administration',
          items: [
            {
              id: 'quota',
              label: 'Usage and credits',
              shortLabel: 'Usage',
              icon: 'credit',
              link: ['/usage'],
            },
          ],
        },
      ]);
      attachedRoot = fixture.nativeElement as HTMLElement;
      document.body.append(attachedRoot);
      await fixture.whenStable();
      const link = attachedRoot.querySelector<HTMLAnchorElement>('a')!;
      expect(link.textContent?.trim()).toBe('Usage');
      expect(link.getAttribute('aria-label')).toBe('Usage and credits');
      if (trigger === 'focus') link.focus();
      else {
        const event = new Event('pointerenter');
        Object.defineProperty(event, 'pointerType', { value: 'mouse' });
        link.dispatchEvent(event);
      }
      await vi.waitFor(() =>
        expect(document.querySelector('[role="tooltip"]')?.textContent?.trim()).toBe(
          'Usage and credits',
        ),
      );
      expect(document.querySelector('[role="tooltip"] span')?.getAttribute('aria-hidden')).toBe(
        'true',
      );
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await vi.waitFor(() => expect(document.querySelector('[role="tooltip"]')).toBeNull());
      if (trigger === 'focus') expect(document.activeElement).toBe(link);
    },
  );

  it('does not add a tooltip when a short label equals the complete label', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', [
      {
        ...groups[0],
        items: [{ ...groups[0]!.items[0]!, shortLabel: 'Overview' }],
      },
    ]);
    attachedRoot = fixture.nativeElement as HTMLElement;
    document.body.append(attachedRoot);
    await fixture.whenStable();
    attachedRoot.querySelector<HTMLAnchorElement>('a')!.focus();
    await fixture.whenStable();
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
  });

  it('keeps expanded navigation labels visible without a redundant tooltip', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    attachedRoot = fixture.nativeElement as HTMLElement;
    document.body.append(attachedRoot);
    await fixture.whenStable();
    const link = attachedRoot.querySelector<HTMLAnchorElement>('a')!;
    link.focus();
    await fixture.whenStable();
    expect(link.textContent?.trim()).toBe('Overview');
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
  });

  describe('settings item', () => {
    const settings: NavSettingsItem = {
      label: 'Settings',
      link: ['/orgs', 'o', 'settings'],
      covers: [
        ['/orgs', 'o', 'settings'],
        ['/orgs', 'o', 'members'],
        ['/orgs', 'o', 'audit'],
      ],
    };

    async function renderAt(url: string) {
      const fixture = TestBed.createComponent(NavComponent);
      fixture.componentRef.setInput('groups', []);
      fixture.componentRef.setInput('settings', settings);
      attachedRoot = fixture.nativeElement as HTMLElement;
      // 3px marker (::before) is verified in a real browser by e2e/shell/navigation.spec.ts.
      document.body.append(attachedRoot);
      await TestBed.inject(Router).navigateByUrl(url);
      await fixture.whenStable();
      return attachedRoot.querySelector<HTMLAnchorElement>('a')!;
    }

    it.each([
      '/orgs/o/settings/members',
      '/orgs/o/settings/audit',
      '/orgs/o/members',
      '/orgs/o/audit',
    ])('marks Settings as the current page on %s', async (url) => {
      const link = await renderAt(url);
      expect(link.getAttribute('aria-current')).toBe('page');
    });

    it.each(['/orgs/o/projects', '/orgs/o/settingsx', '/orgs/other/settings'])(
      'does not activate Settings on unrelated URL %s',
      async (url) => {
        const link = await renderAt(url);
        expect(link.getAttribute('aria-current')).toBeNull();
      },
    );

    it('ignores query and fragment when matching a covered destination', async () => {
      const link = await renderAt('/orgs/o/audit?tab=events#top');
      expect(link.getAttribute('aria-current')).toBe('page');
    });
  });
});

// 09.1.1-T4.1/T4.2; docs/design/web/layout.md#breakpoints-and-navigation.
// Real 360px overflow, computed contrast and screen-reader evidence belong to browser QA.
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { Mock } from 'vitest';
import { CdkTrapFocus } from '@angular/cdk/a11y';
import { ScopeSwitcherComponent } from './scope-switcher.component';
import { NavigationEnd, Router, provideRouter } from '@angular/router';
import axe from 'axe-core';
import { AXE_JSDOM_RUN_OPTIONS } from '../../../../e2e/a11y/axe-config';
import { SessionStore } from '../../core/session/session.store';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { NavViewService } from '../nav/nav-view';
import type { NavGroupView } from '../nav/nav.models';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellComponent } from './shell.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS, stubScopeBrowserApis } from './scope.fixture';

const groups: readonly NavGroupView[] = [
  {
    id: 'design',
    label: 'Test design',
    items: [
      {
        id: 'overview',
        label: 'Overview',
        icon: 'home',
        link: ['/orgs', 'org-one', 'projects', 'project-one', 'overview'],
      },
    ],
  },
];

describe('ShellComponent', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let root: HTMLElement;
  let restoreBrowserApis: () => void;
  const session = {
    organizations: signal(SCOPE_ORGANIZATIONS),
    projects: signal(SCOPE_PROJECTS),
    currentOrg: signal(SCOPE_ORGANIZATIONS[0]),
    currentProject: signal(SCOPE_PROJECTS[0]),
  };

  beforeEach(async () => {
    restoreBrowserApis = stubScopeBrowserApis();
    vi.stubGlobal('innerWidth', 1440);
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      deferBlockBehavior: DeferBlockBehavior.Manual,
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: NotFoundPage }]),
        { provide: SessionStore, useValue: session },
        { provide: QuotaStore, useValue: { badge: signal(null), load: vi.fn() } },
        { provide: NavViewService, useValue: { groups: signal(groups) } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ShellComponent);
    root = fixture.nativeElement as HTMLElement;
    document.body.append(root);
    await fixture.whenStable();
    await renderScope();
  });

  afterEach(() => {
    fixture.destroy();
    root.remove();
    restoreBrowserApis();
    vi.unstubAllGlobals();
    document.documentElement.removeAttribute('data-theme');
    localStorage.removeItem('evidgate-theme');
  });

  async function renderScope(): Promise<void> {
    for (const block of await fixture.getDeferBlocks()) {
      await block.render(DeferBlockState.Complete);
    }
    await fixture.whenStable();
  }

  it('provides the skip target, main landmark, named nav and router outlet', () => {
    expect(root.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main-content');
    expect(root.querySelector('a.skip-link')?.classList.contains('transition-none')).toBe(true);
    expect(root.querySelector('main#main-content')?.getAttribute('tabindex')).toBe('-1');
    expect(root.querySelector('nav[aria-label="Primary navigation"]')).not.toBeNull();
    expect(root.querySelector('router-outlet')).not.toBeNull();
    expect(root.textContent).not.toContain('QA engineer');
  });

  it('keeps the topbar keyboard stops in the order the header reads', () => {
    const topbar = root.querySelector<HTMLElement>('header.topbar')!;
    const stops = Array.from(topbar.querySelectorAll<HTMLElement>('button'));
    expect(stops).toHaveLength(4);
    expect(stops[0]?.classList.contains('nav-toggle')).toBe(true);
    expect(stops[1]?.getAttribute('role')).toBe('combobox');
    expect(stops[2]?.getAttribute('role')).toBe('combobox');
    expect(stops[3]?.classList.contains('theme-toggle')).toBe(true);

    const theme = topbar.querySelector<HTMLElement>('button.theme-toggle')!;
    const scope = topbar.querySelector<HTMLElement>('app-scope-switcher')!;
    expect(scope.classList.contains('compact')).toBe(true);
    expect(scope.compareDocumentPosition(theme) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('measures the topbar so the focus scroll offset follows its wrapped height', () => {
    // The stylesheet's `scroll-margin-top` reads `--eg-topbar-height`; the shell is what publishes
    // it, because the bar's height is a layout result (it wraps) rather than a token.
    const header = root.querySelector<HTMLElement>('header.topbar')!;
    const observers = vi
      .mocked(ResizeObserver)
      .mock.results.map((result) => result.value as ResizeObserver);
    const watching = observers.filter((observer) => {
      const observe = Reflect.get(observer, 'observe') as Mock;
      return observe.mock.calls.some((call) => call[0] === header);
    });
    expect(watching).toHaveLength(1);
  });

  it.each([360, 390, 767])('moves scope controls into the drawer at %ipx', async (width) => {
    vi.stubGlobal('innerWidth', width);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    await renderScope();
    const topbar = root.querySelector('header.topbar');
    expect(topbar?.querySelectorAll('[role="combobox"]')).toHaveLength(0);
    expect(topbar?.querySelectorAll('button')).toHaveLength(2);
    expect(root.querySelectorAll('app-scope-switcher')).toHaveLength(1);
    const aside = root.querySelector<HTMLElement>('aside');
    expect(aside?.querySelectorAll('[role="combobox"]')).toHaveLength(2);
    expect(aside?.hasAttribute('inert')).toBe(true);
    root.querySelector<HTMLButtonElement>('.nav-toggle')?.click();
    await fixture.whenStable();
    expect(aside?.hasAttribute('inert')).toBe(false);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.debugElement
      .query(By.directive(ScopeSwitcherComponent))
      .injector.get(ScopeSwitcherComponent)
      .orgSelected.emit('org-two');
    expect(navigate).toHaveBeenCalledWith(['/orgs', 'org-two']);
  });

  it('restores compact scope in the topbar when leaving the narrow layout', async () => {
    vi.stubGlobal('innerWidth', 390);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    vi.stubGlobal('innerWidth', 1440);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    await renderScope();
    expect(root.querySelector('aside app-scope-switcher')).toBeNull();
    expect(root.querySelectorAll('header.topbar [role="combobox"]')).toHaveLength(2);
    expect(root.querySelectorAll('app-scope-switcher')).toHaveLength(1);
  });

  it('uses expanded/rail natural modes and preserves a user override on resize', async () => {
    expect(fixture.componentInstance.collapsed()).toBe(false);
    vi.stubGlobal('innerWidth', 768);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    expect(fixture.componentInstance.collapsed()).toBe(true);
    (root.querySelector('.nav-toggle') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.componentInstance.collapsed()).toBe(false);
    vi.stubGlobal('innerWidth', 1000);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    expect(fixture.componentInstance.collapsed()).toBe(false);
  });

  it('opens a modal drawer, contains Tab, closes on Escape and restores toggle focus', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    const toggle = root.querySelector<HTMLButtonElement>('.nav-toggle')!;
    toggle.focus();
    toggle.click();
    await fixture.whenStable();
    const close = root.querySelector<HTMLButtonElement>('.close-button')!;
    const aside = root.querySelector<HTMLElement>('aside')!;
    expect(document.activeElement).toBe(close);
    expect(aside.getAttribute('role')).toBe('dialog');
    expect(aside.getAttribute('aria-modal')).toBe('true');
    // The dialog and the `<nav>` inside it carry different names: two landmarks with the same name
    // are announced twice with no way to tell them apart (review L5).
    expect(aside.getAttribute('aria-label')).toBe('Navigation drawer');
    expect(aside.querySelector('nav')?.getAttribute('aria-label')).toBe('Primary navigation');
    expect(root.querySelector('.main-stack')?.hasAttribute('inert')).toBe(true);
    const trap = fixture.debugElement.query(By.directive(CdkTrapFocus)).injector.get(CdkTrapFocus);
    expect(trap.enabled).toBe(true);
    expect(root.querySelectorAll('.cdk-focus-trap-anchor')).toHaveLength(2);
    close.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
    expect(root.querySelector('.main-stack')?.hasAttribute('inert')).toBe(false);
    expect(aside.hasAttribute('inert')).toBe(true);
    expect(document.activeElement).toBe(toggle);
  });

  it('leaves the drawer open for the Escape that closes a scope popup, then closes it on the next', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    await renderScope();
    root.querySelector<HTMLButtonElement>('.nav-toggle')!.click();
    await fixture.whenStable();
    const aside = root.querySelector<HTMLElement>('aside')!;
    const org = aside.querySelector<HTMLButtonElement>('[role="combobox"]')!;
    org.focus();
    org.click();
    await fixture.whenStable();
    expect(org.getAttribute('aria-expanded')).toBe('true');
    // The popup flushes its closed state for that Escape before the drawer's handler runs, so a
    // rendered `aria-expanded` probe misses it there: close and re-render at the aside's capture.
    aside.addEventListener(
      'keydown',
      () => {
        org.click();
        fixture.detectChanges();
      },
      { capture: true, once: true },
    );
    const pressEscape = (): void => {
      org.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      );
    };
    pressEscape();
    await fixture.whenStable();
    expect(org.getAttribute('aria-expanded')).toBe('false');
    expect(fixture.componentInstance.drawerOpen()).toBe(true);
    pressEscape();
    await fixture.whenStable();
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
  });

  it('waits for the visible, non-inert drawer before focusing its close button', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    const aside = root.querySelector<HTMLElement>('aside')!;
    const close = root.querySelector<HTMLButtonElement>('.close-button')!;
    const nativeFocus = close.focus.bind(close);
    const focus = vi.spyOn(close, 'focus').mockImplementation(() => {
      expect(aside.hasAttribute('inert')).toBe(false);
      expect(aside.classList.contains('opacity-0')).toBe(false);
      nativeFocus();
    });

    fixture.componentInstance.toggleNavigation();
    expect(focus).not.toHaveBeenCalled();
    await fixture.whenStable();
    expect(focus).toHaveBeenCalledOnce();
  });

  it('does not focus the close button if the drawer closes before its render', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    const close = root.querySelector<HTMLButtonElement>('.close-button')!;
    const focus = vi.spyOn(close, 'focus');
    fixture.componentInstance.toggleNavigation();
    fixture.componentInstance.closeDrawer();
    await fixture.whenStable();
    expect(focus).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(root.querySelector('.nav-toggle'));
  });

  it('retries a refused focus on a frame and stops after the control receives focus', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    const frames: FrameRequestCallback[] = [];
    const close = root.querySelector<HTMLButtonElement>('.close-button')!;
    const nativeFocus = close.focus.bind(close);
    const focus = vi.spyOn(close, 'focus').mockImplementationOnce(() => undefined);
    fixture.componentInstance.toggleNavigation();
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) =>
      frames.push(callback),
    );
    await fixture.whenStable();
    expect(focus).toHaveBeenCalledOnce();
    expect(frames).toHaveLength(1);
    focus.mockImplementation(nativeFocus);
    frames.shift()!(0);
    expect(document.activeElement).toBe(close);
    expect(frames).toHaveLength(0);
  });

  it('bounds refused focus retries to three frames', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    const frames: FrameRequestCallback[] = [];
    const focus = vi
      .spyOn(root.querySelector<HTMLButtonElement>('.close-button')!, 'focus')
      .mockImplementation(() => undefined);
    fixture.componentInstance.toggleNavigation();
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) =>
      frames.push(callback),
    );
    await fixture.whenStable();
    for (let frame = 0; frame < 3; frame++) frames.shift()!(frame);
    expect(focus).toHaveBeenCalledTimes(4);
    expect(frames).toHaveLength(0);
  });

  it('keeps focus on main when a drawer link revisits an already-loaded destination', async () => {
    const router = TestBed.inject(Router);
    const destination = '/orgs/org-one/projects/project-one/overview';
    await router.navigateByUrl(destination);
    await router.navigateByUrl('/other');
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    fixture.componentInstance.toggleNavigation();
    await fixture.whenStable();
    const link = root.querySelector<HTMLAnchorElement>('aside nav a')!;
    link.click();
    await fixture.whenStable();
    expect(router.url).toBe(destination);
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
    expect(document.activeElement).toBe(root.querySelector('#main-content'));
  });

  it.each(['', '?filter=recent#details'])(
    'restores opener focus when a drawer link selects the current path from suffix "%s"',
    async (suffix) => {
      const router = TestBed.inject(Router);
      const destination = '/orgs/org-one/projects/project-one/overview';
      await router.navigateByUrl(destination + suffix);
      vi.stubGlobal('innerWidth', 360);
      window.dispatchEvent(new Event('resize'));
      await fixture.whenStable();
      fixture.componentInstance.toggleNavigation();
      await fixture.whenStable();
      const link = root.querySelector<HTMLAnchorElement>('aside nav a')!;
      link.focus();
      link.click();
      await fixture.whenStable();
      expect(router.url).toBe(destination);
      expect(fixture.componentInstance.drawerOpen()).toBe(false);
      expect(document.activeElement).toBe(root.querySelector('.nav-toggle'));
    },
  );

  it('keeps a scope placeholder until the deferred mobile switcher is ready', async () => {
    vi.stubGlobal('innerWidth', 360);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    const aside = root.querySelector<HTMLElement>('aside')!;
    expect(aside.querySelector('app-scope-switcher')).toBeNull();
    expect(aside.querySelector('.drawer-scope[aria-hidden="true"]')).not.toBeNull();
    await renderScope();
    expect(aside.querySelector('.drawer-scope[aria-hidden="true"]')).toBeNull();
    expect(aside.querySelectorAll('[role="combobox"]')).toHaveLength(2);
  });

  it('closes the modal on resize beyond the drawer boundary', async () => {
    vi.stubGlobal('innerWidth', 767);
    window.dispatchEvent(new Event('resize'));
    fixture.componentInstance.toggleNavigation();
    await fixture.whenStable();
    vi.stubGlobal('innerWidth', 768);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
    expect(root.querySelector('[aria-modal="true"]')).toBeNull();
  });

  it('uses the router for organization selection without changing scope signals', async () => {
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.componentInstance.selectOrganization('org-two');
    expect(navigate).toHaveBeenCalledWith(['/orgs', 'org-two']);
    expect(session.currentOrg().id).toBe('org-one');
    await fixture.whenStable();
  });

  it('replaces the project id, keeping the entry segment, query and fragment', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl(
      '/orgs/org-one/projects/project-one/authoring/node;view=tree?filter=x#details',
    );
    await fixture.whenStable();
    fixture.componentInstance.selectProject('project-two');
    await fixture.whenStable();
    expect(router.url).toBe('/orgs/org-one/projects/project-two/authoring?filter=x#details');
    expect(session.currentProject().id).toBe('project-one');
  });

  it('drops the deep tail of the previous project (a run id belongs to that project)', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/orgs/org-one/projects/project-one/runs/run-42?filter=x#details');
    await fixture.whenStable();
    fixture.componentInstance.selectProject('project-two');
    await fixture.whenStable();
    expect(router.url).toBe('/orgs/org-one/projects/project-two/runs?filter=x#details');
  });

  it('drops the query on an organization switch but keeps it on a project switch', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/orgs/org-one/projects/project-one/audit?filter=x#details');
    await fixture.whenStable();

    fixture.componentInstance.selectOrganization('org-two');
    await fixture.whenStable();
    // The other organization has no such project, page, query or fragment: the whole scope is new.
    expect(router.url).toBe('/orgs/org-two');

    await router.navigateByUrl('/orgs/org-one/projects/project-one/audit?filter=x#details');
    fixture.componentInstance.selectProject('project-two');
    await fixture.whenStable();
    expect(router.url).toBe('/orgs/org-one/projects/project-two/audit?filter=x#details');
  });

  it('uses overview when selecting a project from an org-only route', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/orgs/org-one/settings');
    fixture.componentInstance.selectProject('project-two');
    await fixture.whenStable();
    expect(router.url).toBe('/orgs/org-one/projects/project-two/overview');
  });

  it('does not steal focus on first navigation; later navigations focus main and update title', async () => {
    const router = TestBed.inject(Router);
    const toggle = root.querySelector<HTMLButtonElement>('.nav-toggle')!;
    toggle.focus();
    await router.navigateByUrl('/first');
    await fixture.whenStable();
    expect(document.activeElement).toBe(toggle);
    await router.navigateByUrl('/second');
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('main'));
    expect(document.title).toContain('Not found');
  });

  it('keeps focus for a query-only or fragment-only navigation, moves it on a path change', async () => {
    const router = TestBed.inject(Router);
    const navigations: string[] = [];
    router.events.subscribe((event) => {
      if (event instanceof NavigationEnd) navigations.push(event.urlAfterRedirects);
    });
    const toggle = root.querySelector<HTMLButtonElement>('.nav-toggle')!;
    toggle.focus();
    await router.navigateByUrl('/first');
    await fixture.whenStable();
    await router.navigateByUrl('/first?page=2');
    await fixture.whenStable();
    // The query-only navigation really happened, so the focus assertion below is not vacuous.
    expect(navigations).toContain('/first?page=2');
    expect(document.activeElement).toBe(toggle);
    await router.navigateByUrl('/first?page=2#section');
    await fixture.whenStable();
    expect(navigations).toContain('/first?page=2#section');
    expect(document.activeElement).toBe(toggle);
    await router.navigateByUrl('/second');
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('main'));
  });

  it('passes the shell structure/ARIA axe checks (jsdom cannot measure contrast)', async () => {
    await TestBed.inject(Router).navigateByUrl('/orgs/org-one/projects/project-one/overview');
    await fixture.whenStable();
    const results = await axe.run(root, AXE_JSDOM_RUN_OPTIONS);
    expect(
      results.violations.map((violation) => ({
        id: violation.id,
        nodes: violation.nodes.map((node) => node.target),
      })),
    ).toEqual([]);
  }, 30_000);
});

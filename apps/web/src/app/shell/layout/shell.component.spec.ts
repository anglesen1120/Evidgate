// 09.1.1-T4.1/T4.2; docs/design/web/layout.md#breakpoints-and-navigation.
// Real 360px overflow, computed contrast and screen-reader evidence belong to browser QA.
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { NavigationEnd, Router, provideRouter } from '@angular/router';
import axe from 'axe-core';
import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../nav/nav-view';
import type { NavGroupView } from '../nav/nav.models';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellComponent } from './shell.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS } from './scope.fixture';

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
  const session = {
    organizations: signal(SCOPE_ORGANIZATIONS),
    projects: signal(SCOPE_PROJECTS),
    currentOrg: signal(SCOPE_ORGANIZATIONS[0]),
    currentProject: signal(SCOPE_PROJECTS[0]),
  };

  beforeEach(async () => {
    vi.stubGlobal('innerWidth', 1440);
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: NotFoundPage }]),
        { provide: SessionStore, useValue: session },
        { provide: NavViewService, useValue: { groups: signal(groups) } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ShellComponent);
    root = fixture.nativeElement as HTMLElement;
    document.body.append(root);
    await fixture.whenStable();
  });

  afterEach(() => {
    fixture.destroy();
    root.remove();
    vi.unstubAllGlobals();
    document.documentElement.removeAttribute('data-theme');
    localStorage.removeItem('evidgate-theme');
  });

  it('provides the skip target, main landmark, named nav and router outlet', () => {
    expect(root.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main-content');
    expect(root.querySelector('main#main-content')?.getAttribute('tabindex')).toBe('-1');
    expect(root.querySelector('nav[aria-label="Primary navigation"]')).not.toBeNull();
    expect(root.querySelector('router-outlet')).not.toBeNull();
    expect(root.textContent).not.toContain('QA engineer');
  });

  // F-SWITCH-01 (09.8.1-T1.2, WCAG 2.4.3): below 768px the two scope selects sit on a second row
  // while the theme toggle stays on the first, next to the navigation toggle. CSS `order` reorders
  // paint only, so the header's document order is the browser's Tab order and must agree with what
  // the topbar looks like at every width: navigation toggle → theme toggle → organization →
  // project. jsdom has no sequential focus navigation, so the document order is what this pins; the
  // geometry of the two rows is pinned in Chromium by `e2e/shell/a11y-keyboard-scope.spec.ts`.
  it('keeps the topbar keyboard stops in the order the header reads', () => {
    const topbar = root.querySelector<HTMLElement>('header.topbar')!;
    const stops = Array.from(topbar.querySelectorAll<HTMLElement>('button, select')).map(
      (element) => element.className || element.tagName.toLowerCase(),
    );
    expect(stops).toEqual([
      'icon-button nav-toggle',
      'icon-button theme-toggle',
      'select',
      'select',
    ]);

    const theme = topbar.querySelector<HTMLElement>('button.theme-toggle')!;
    const scope = topbar.querySelector<HTMLElement>('app-scope-switcher')!;
    expect(theme.compareDocumentPosition(scope) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // The CSS reorder that caused the finding is gone (the scope group is last in the document too),
    // and nothing here can reintroduce one: jsdom does not evaluate the 767px media block, so the
    // paint order at both widths is pinned in Chromium by `e2e/shell/a11y-keyboard-scope.spec.ts`.
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
    const last = Array.from(aside.querySelectorAll<HTMLAnchorElement>('a')).at(-1)!;
    close.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }),
    );
    expect(document.activeElement).toBe(last);
    last.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }),
    );
    expect(document.activeElement).toBe(close);
    close.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
    expect(root.querySelector('.main-stack')?.hasAttribute('inert')).toBe(false);
    expect(aside.hasAttribute('inert')).toBe(true);
    expect(document.activeElement).toBe(toggle);
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
    const results = await axe.run(root, {
      runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
      rules: { 'color-contrast': { enabled: false } },
    });
    expect(
      results.violations.map((violation) => ({
        id: violation.id,
        nodes: violation.nodes.map((node) => node.target),
      })),
    ).toEqual([]);
  }, 30_000);
});

// 09.1.1-T4.1/T4.2; docs/design/web/layout.md#breakpoints-and-navigation.
// Real 360px overflow, computed contrast and screen-reader evidence belong to browser QA.
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
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

  it('replaces only the project id, preserving the remaining path, query and fragment', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl(
      '/orgs/org-one/projects/project-one/authoring/node;view=tree?filter=x#details',
    );
    await fixture.whenStable();
    fixture.componentInstance.selectProject('project-two');
    await fixture.whenStable();
    expect(router.url).toBe(
      '/orgs/org-one/projects/project-two/authoring/node;view=tree?filter=x#details',
    );
    expect(session.currentProject().id).toBe('project-one');
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

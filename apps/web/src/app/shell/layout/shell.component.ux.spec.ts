import { provideZonelessChangeDetection, signal } from '@angular/core';
import {
  ComponentFixture,
  DeferBlockBehavior,
  DeferBlockState,
  TestBed,
} from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ViewportRuler } from '@angular/cdk/scrolling';
import type { ProjectDto } from '@evidgate/sdk';
import { SessionStore } from '../../core/session/session.store';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { NavViewService } from '../nav/nav-view';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellComponent } from './shell.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS, stubScopeBrowserApis } from './scope.fixture';

describe('ShellComponent scope, locale and drawer context', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let root: HTMLElement;
  let restoreBrowserApis: () => void;
  const project = signal<ProjectDto | null>(SCOPE_PROJECTS[0]);

  beforeEach(async () => {
    restoreBrowserApis = stubScopeBrowserApis();
    project.set(SCOPE_PROJECTS[0]);
    vi.stubGlobal('innerWidth', 1440);
    vi.spyOn(document, 'baseURI', 'get').mockReturnValue('https://evidgate.test/en/');
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      deferBlockBehavior: DeferBlockBehavior.Manual,
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: NotFoundPage }]),
        {
          provide: SessionStore,
          useValue: {
            organizations: signal(SCOPE_ORGANIZATIONS),
            projects: signal(SCOPE_PROJECTS),
            currentOrg: signal(SCOPE_ORGANIZATIONS[0]),
            currentProject: project,
          },
        },
        { provide: QuotaStore, useValue: { badge: signal(null), load: vi.fn() } },
        { provide: NavViewService, useValue: { groups: signal([]) } },
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
    restoreBrowserApis();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function resize(width: number): Promise<void> {
    vi.stubGlobal('innerWidth', width);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
  }

  function scrollablePage(top = 3000, left = 0) {
    const viewport = TestBed.inject(ViewportRuler);
    vi.spyOn(viewport, 'getViewportSize').mockReturnValue({ width: 390, height: 900 });
    vi.spyOn(viewport, 'getViewportScrollPosition').mockReturnValue({ top, left });
    vi.spyOn(document.documentElement, 'scrollHeight', 'get').mockReturnValue(18680);
    return vi.spyOn(window, 'scroll').mockImplementation(() => undefined);
  }

  it('shows the organization alone on an org route and the project pair only when selected', async () => {
    const scope = root.querySelector('.nav-scope')!;
    expect(scope.querySelectorAll('strong')).toHaveLength(2);
    expect(scope.textContent).toContain(SCOPE_PROJECTS[0].name);
    project.set(null);
    await fixture.whenStable();
    expect(scope.querySelectorAll('strong')).toHaveLength(1);
    expect(scope.textContent).toContain(SCOPE_ORGANIZATIONS[0].name);
    expect(scope.textContent).not.toContain('Project');
    project.set(SCOPE_PROJECTS[1]);
    await fixture.whenStable();
    expect(scope.querySelectorAll('strong')).toHaveLength(2);
    expect(scope.textContent).toContain(SCOPE_PROJECTS[1].name);
  });

  it('lets both tablet scope states grow within the remaining toolbar budget', async () => {
    await resize(768);
    const expectFlexibleScope = (element: Element | null) => {
      expect(element?.classList.contains('min-w-0')).toBe(true);
      expect(element?.classList.contains('flex-1')).toBe(true);
      expect(element?.classList.contains('basis-0')).toBe(true);
      expect(element?.classList.contains('max-w-[calc(1.5*var(--eg-rail-width))]')).toBe(true);
    };
    expectFlexibleScope(root.querySelector('header .topbar-scope'));
    const scopeBlocks = await fixture.getDeferBlocks();
    expect(scopeBlocks).toHaveLength(1);
    await scopeBlocks[0]!.render(DeferBlockState.Complete);
    await fixture.whenStable();
    expectFlexibleScope(root.querySelector('header app-scope-switcher'));
    expect(root.querySelectorAll('header [role="combobox"]')).toHaveLength(2);
    expect(root.querySelector('header .locale-toggle')).not.toBeNull();
    expect(root.querySelector('header .theme-toggle')).not.toBeNull();
    await resize(767);
    expect(root.querySelector('header .topbar-scope')).toBeNull();
    expect(root.querySelector('aside .locale-toggle')).not.toBeNull();
  });

  it('keeps one labelled locale control in the topbar, moving it into the mobile drawer', async () => {
    expect(root.querySelectorAll('.locale-toggle')).toHaveLength(1);
    expect(root.querySelector('header .locale-toggle')?.getAttribute('aria-label')).toContain(
      'EN / VI',
    );
    await resize(767);
    expect(root.querySelector('header .locale-toggle')).toBeNull();
    expect(root.querySelectorAll('aside .locale-toggle')).toHaveLength(1);
    root.querySelector<HTMLButtonElement>('.nav-toggle')!.click();
    await fixture.whenStable();
    expect(root.querySelector('aside')?.hasAttribute('inert')).toBe(false);
    expect(root.querySelector<HTMLButtonElement>('aside .locale-toggle')?.disabled).toBe(false);
    await resize(768);
    expect(root.querySelectorAll('header .locale-toggle')).toHaveLength(1);
    expect(root.querySelector('aside .locale-toggle')).toBeNull();
  });

  it.each(['Escape', 'close button', 'scrim'])(
    'preserves the reading position when the drawer closes through %s',
    async (action) => {
      await resize(390);
      const scroll = scrollablePage(3000, 12);
      const toggle = root.querySelector<HTMLButtonElement>('.nav-toggle')!;
      const focus = vi.spyOn(toggle, 'focus');
      toggle.click();
      await fixture.whenStable();
      expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(true);
      expect(document.documentElement.style.top).toBe('-3000px');
      expect(document.documentElement.style.left).toBe('-12px');
      expect(root.querySelector('.shell')?.classList.contains('[&.drawer-open]:h-dvh')).toBe(false);
      if (action === 'Escape') {
        root
          .querySelector('.close-button')!
          .dispatchEvent(
            new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
          );
      } else {
        root
          .querySelector<HTMLButtonElement>(action === 'scrim' ? '.scrim' : '.close-button')!
          .click();
      }
      await fixture.whenStable();
      expect(fixture.componentInstance.drawerOpen()).toBe(false);
      expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(false);
      expect(scroll).toHaveBeenLastCalledWith(12, 3000);
      expect(document.activeElement).toBe(toggle);
      expect(focus).toHaveBeenLastCalledWith({ preventScroll: true });
    },
  );

  it('loads the other language bundle for the equivalent route with its query and fragment', async () => {
    const router = TestBed.inject(Router);
    const route = '/orgs/org-one/projects/project-one/runs/run-one?filter=a%2Fb&page=2#evidence';
    await router.navigateByUrl(route);
    await fixture.whenStable();
    const navigate = vi.fn();
    const view = new Proxy(window, {
      get(target, property, receiver): unknown {
        if (property === 'location') return { assign: navigate };
        const value: unknown = Reflect.get(target, property, receiver);
        return value;
      },
    });
    vi.spyOn(document, 'defaultView', 'get').mockReturnValue(view);
    root.querySelector<HTMLButtonElement>('.locale-toggle')!.click();
    expect(navigate).toHaveBeenCalledExactlyOnceWith('/vi' + route);
    expect(router.url).toBe(route);
  });

  it('does not offer language switching when there is no localized sibling bundle', async () => {
    vi.spyOn(document, 'baseURI', 'get').mockReturnValue('https://evidgate.test/preview/');
    const unlocalized = TestBed.createComponent(ShellComponent);
    await unlocalized.whenStable();
    expect((unlocalized.nativeElement as HTMLElement).querySelector('.locale-toggle')).toBeNull();
    unlocalized.destroy();
  });

  it('leaves non-scrollable pages unlocked and still restores keyboard focus', async () => {
    await resize(390);
    const scroll = vi.spyOn(window, 'scroll').mockImplementation(() => undefined);
    vi.spyOn(document.documentElement, 'scrollHeight', 'get').mockReturnValue(0);
    root.querySelector<HTMLButtonElement>('.nav-toggle')!.click();
    await fixture.whenStable();
    expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(false);
    root.querySelector<HTMLButtonElement>('.close-button')!.click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('.nav-toggle'));
    expect(scroll).not.toHaveBeenCalled();
  });

  it('releases the lock on navigation without stealing focus back from the destination', async () => {
    await resize(390);
    scrollablePage();
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/first');
    root.querySelector<HTMLButtonElement>('.nav-toggle')!.click();
    await fixture.whenStable();
    expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(true);
    await router.navigateByUrl('/second');
    await fixture.whenStable();
    expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(false);
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
    expect(document.activeElement).toBe(root.querySelector('main'));
  });

  it('unlocks and restores the saved position on resize and when the shell is destroyed', async () => {
    await resize(767);
    const scroll = scrollablePage();
    root.querySelector<HTMLButtonElement>('.nav-toggle')!.click();
    await fixture.whenStable();
    await resize(768);
    expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(false);
    expect(scroll).toHaveBeenLastCalledWith(0, 3000);
    await resize(390);
    root.querySelector<HTMLButtonElement>('.nav-toggle')!.click();
    await fixture.whenStable();
    expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(true);
    fixture.destroy();
    expect(document.documentElement.classList.contains('cdk-global-scrollblock')).toBe(false);
    expect(scroll).toHaveBeenLastCalledWith(0, 3000);
  });
});

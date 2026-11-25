import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
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
import { IdentityDataService } from '../../core/data/identity-data.service';
import { SessionStore } from '../../core/session/session.store';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellComponent } from './shell.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS, stubScopeBrowserApis } from './scope.fixture';

class ShellScopeHarness extends ComponentHarness {
  static hostSelector = 'app-shell';
  readonly scopeValues = this.locatorForAll('header [role="combobox"] bdi');
  readonly selects = this.locatorForAll('header hlm-select');
  readonly staticCrumbs = this.locatorForAll('main > app-shell-breadcrumbs li > bdi');
  readonly account = this.locatorFor('header app-shell-account button');
}

class LanguageMenu extends ComponentHarness {
  static hostSelector = '[role="menu"]';
  readonly vietnamese = this.locatorForOptional('button[lang="vi"]');
}

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
            has: () => false,
          },
        },
        {
          provide: IdentityDataService,
          useValue: { getMyProfile: vi.fn().mockResolvedValue({ display_name: null }) },
        },
        { provide: QuotaStore, useValue: { badge: signal(null), load: vi.fn() } },
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

  it('keeps a single scope trail above the routed content on desktop and mobile', async () => {
    const shell = await TestbedHarnessEnvironment.harnessForFixture(fixture, ShellScopeHarness);
    const [scopeBlock] = await fixture.getDeferBlocks();
    await scopeBlock?.render(DeferBlockState.Complete);
    const values = async () =>
      Promise.all((await shell.scopeValues()).map((value) => value.text()));
    expect(await values()).toEqual([SCOPE_ORGANIZATIONS[0].name, SCOPE_PROJECTS[0].name]);
    expect(root.querySelector('header app-shell-breadcrumbs')).toBeNull();
    expect(root.querySelector('main')?.firstElementChild?.tagName).toBe('APP-SHELL-BREADCRUMBS');
    expect(await Promise.all((await shell.staticCrumbs()).map((part) => part.text()))).toEqual([
      SCOPE_ORGANIZATIONS[0].name,
      SCOPE_PROJECTS[0].name,
    ]);
    project.set(null);
    expect(await values()).not.toContain(SCOPE_PROJECTS[0].name);
    expect(await (await shell.selects())[1]?.getAttribute('data-value')).toBe('');
    expect(await (await shell.scopeValues())[0]?.text()).toBe(SCOPE_ORGANIZATIONS[0].name);
    project.set(SCOPE_PROJECTS[1]);
    expect(await values()).toEqual([SCOPE_ORGANIZATIONS[0].name, SCOPE_PROJECTS[1].name]);
    await resize(390);
    expect(await shell.scopeValues()).toHaveLength(0);
    expect(await Promise.all((await shell.staticCrumbs()).map((part) => part.text()))).toEqual([
      SCOPE_ORGANIZATIONS[0].name,
      SCOPE_PROJECTS[1].name,
    ]);
  });

  it('lets both tablet scope states grow within the remaining toolbar budget', async () => {
    await resize(768);
    expect(root.querySelector('header .topbar-scope [aria-hidden="true"]')).not.toBeNull();
    const scopeBlocks = await fixture.getDeferBlocks();
    expect(scopeBlocks).toHaveLength(1);
    await scopeBlocks[0]!.render(DeferBlockState.Complete);
    await fixture.whenStable();
    expect(root.querySelector('header app-scope-switcher')).not.toBeNull();
    expect(root.querySelectorAll('header [role="combobox"]')).toHaveLength(2);
    expect(root.querySelector('header app-shell-account button')).not.toBeNull();
    expect(root.querySelector('header .theme-toggle')).not.toBeNull();
    await resize(767);
    expect(root.querySelector('header .topbar-scope')).toBeNull();
    expect(root.querySelector('header app-shell-account button')).not.toBeNull();
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
    const shell = await TestbedHarnessEnvironment.harnessForFixture(fixture, ShellScopeHarness);
    await (await shell.account()).click();
    const menu =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(LanguageMenu);
    const vietnamese = await menu.vietnamese();
    expect(vietnamese).not.toBeNull();
    vi.spyOn(document, 'defaultView', 'get').mockReturnValue(view);
    await vietnamese!.click();
    expect(navigate).toHaveBeenCalledExactlyOnceWith('/vi' + route);
    expect(router.url).toBe(route);
  });

  it('does not offer language switching when there is no localized sibling bundle', async () => {
    vi.spyOn(document, 'baseURI', 'get').mockReturnValue('https://evidgate.test/preview/');
    const unlocalized = TestBed.createComponent(ShellComponent);
    await unlocalized.whenStable();
    const shell = await TestbedHarnessEnvironment.harnessForFixture(unlocalized, ShellScopeHarness);
    await (await shell.account()).click();
    const menu =
      await TestbedHarnessEnvironment.documentRootLoader(unlocalized).getHarness(LanguageMenu);
    expect(await menu.vietnamese()).toBeNull();
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

import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { NotFoundPage } from '../pages/not-found.page';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS, stubScopeBrowserApis } from './scope.fixture';
import { ShellComponent } from './shell.component';

class DrawerBoundaryHarness extends ComponentHarness {
  static hostSelector = 'app-shell';
  readonly toggle = this.locatorFor('button.nav-toggle');
  readonly navigation = this.locatorFor('#shell-navigation');
  readonly close = this.locatorFor('button.close-button');
  readonly mainStack = this.locatorFor('.main-stack');
}

describe('Shell drawer responsive keyboard boundaries', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let restoreBrowserApis: () => void;

  beforeEach(async () => {
    restoreBrowserApis = stubScopeBrowserApis();
    vi.stubGlobal('innerWidth', 767);
    vi.spyOn(window, 'scroll').mockImplementation(() => undefined);
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
            currentProject: signal(SCOPE_PROJECTS[0]),
            has: () => false,
          },
        },
        { provide: QuotaStore, useValue: { badge: signal(null), load: vi.fn() } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ShellComponent);
    document.body.append(fixture.nativeElement as HTMLElement);
    await fixture.whenStable();
  });

  afterEach(() => {
    fixture.destroy();
    (fixture.nativeElement as HTMLElement).remove();
    restoreBrowserApis();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('treats 767px as modal and restores the opener after keyboard Escape', async () => {
    const shell = await TestbedHarnessEnvironment.harnessForFixture(fixture, DrawerBoundaryHarness);
    await (await shell.toggle()).click();
    expect(await (await shell.navigation()).getAttribute('role')).toBe('dialog');
    expect(await (await shell.navigation()).getAttribute('aria-modal')).toBe('true');
    expect(await (await shell.mainStack()).getAttribute('inert')).toBe('');
    expect(await (await shell.close()).isFocused()).toBe(true);
    await (await shell.close()).sendKeys(TestKey.ESCAPE);
    expect(await (await shell.navigation()).getAttribute('role')).toBeNull();
    expect(await (await shell.navigation()).getAttribute('inert')).toBe('');
    expect(await (await shell.mainStack()).getAttribute('inert')).toBeNull();
    expect(await (await shell.toggle()).isFocused()).toBe(true);
  });

  it('does not close the drawer for an Escape already consumed by a nested control', async () => {
    const shell = await TestbedHarnessEnvironment.harnessForFixture(fixture, DrawerBoundaryHarness);
    await (await shell.toggle()).click();
    (fixture.nativeElement as HTMLElement).addEventListener(
      'keydown',
      (event) => event.preventDefault(),
      { capture: true, once: true },
    );
    await (await shell.close()).sendKeys(TestKey.ESCAPE);
    expect(await (await shell.navigation()).getAttribute('role')).toBe('dialog');
    await (await shell.close()).sendKeys(TestKey.ESCAPE);
    expect(await (await shell.navigation()).getAttribute('role')).toBeNull();
  });

  it('removes modal and inert state at exactly 768px while retaining usable rail controls', async () => {
    const shell = await TestbedHarnessEnvironment.harnessForFixture(fixture, DrawerBoundaryHarness);
    await (await shell.toggle()).click();
    expect(await (await shell.navigation()).getAttribute('role')).toBe('dialog');
    vi.stubGlobal('innerWidth', 768);
    window.dispatchEvent(new Event('resize'));
    await fixture.whenStable();
    expect(await (await shell.navigation()).getAttribute('role')).toBeNull();
    expect(await (await shell.navigation()).getAttribute('inert')).toBeNull();
    expect(await (await shell.mainStack()).getAttribute('inert')).toBeNull();
    expect(await (await shell.toggle()).isFocused()).toBe(true);
    expect(await (await shell.toggle()).getAttribute('aria-expanded')).toBe('false');
    await (await shell.toggle()).click();
    expect(await (await shell.toggle()).getAttribute('aria-expanded')).toBe('true');
    expect(await (await shell.navigation()).getAttribute('role')).toBeNull();
  });
});

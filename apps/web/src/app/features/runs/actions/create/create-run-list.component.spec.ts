import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../../shared/testing/layout.testing';
import { RunListPage } from '../../list/run-list.page';
import { RunListStore } from '../../list/run-list.store';
import { CreateRunDialogHarness } from './create-run-dialog.test-support';
import { pageStore, runCreatePageProviders } from './create-run-list.test-support';

class RunListCreateHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  private readonly trigger = this.locatorFor('[data-run-create-trigger]');
  async open(): Promise<void> {
    await (await this.trigger()).focus();
    await (await this.trigger()).click();
  }
  async disabled(): Promise<boolean> {
    return (await this.trigger()).getProperty<boolean>('disabled');
  }
  async hasTrigger(): Promise<boolean> {
    return (await this.locatorForOptional('[data-run-create-trigger]')()) !== null;
  }
  async reason(): Promise<string> {
    return (await this.locatorFor('#run-create-permission')()).text();
  }
  async triggerFocused(): Promise<boolean> {
    return (await this.trigger()).isFocused();
  }
}

describe('RunListPage create action', () => {
  provideLayoutTestStubs();
  let state = runCreatePageProviders();
  beforeEach(() => {
    state = runCreatePageProviders(true);
    const project = new BehaviorSubject(convertToParamMap({ projectId: 'project-1' }));
    const query = new BehaviorSubject(convertToParamMap({}));
    const route = {
      pathFromRoot: [{ paramMap: project }],
      queryParamMap: query,
      snapshot: undefined as unknown,
    };
    TestBed.configureTestingModule({
      imports: [RunListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        ...state.providers,
        { provide: ActivatedRoute, useValue: route },
        {
          provide: BreakpointObserver,
          useValue: { observe: () => new BehaviorSubject({ matches: true }) },
        },
      ],
    });
    TestBed.overrideComponent(RunListPage, {
      set: { providers: [{ provide: RunListStore, useValue: pageStore() }] },
    });
    route.snapshot = TestBed.inject(Router).routerState.snapshot.root;
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(RunListPage);
    await fixture.whenStable();
    return {
      fixture,
      page: await TestbedHarnessEnvironment.harnessForFixture(fixture, RunListCreateHarness),
      overlays: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }

  it('opens the real dialog from the list and restores its trigger after Cancel', async () => {
    const { page, overlays } = await render();
    await page.open();
    const dialog = await overlays.getHarness(CreateRunDialogHarness);
    expect(await dialog.value('manifestId')).toBe('');
    expect(state.createRun).not.toHaveBeenCalled();
    await dialog.dismiss();
    expect(await page.triggerFocused()).toBe(true);
    expect(await overlays.hasHarness(CreateRunDialogHarness)).toBe(false);
  });

  it('disables Create run with an adjacent permission reason when run:create is absent', async () => {
    state.allowed.set(false);
    const { page, overlays } = await render();
    expect(await page.disabled()).toBe(true);
    expect(await page.reason()).toContain('run:create');
    await page.open();
    expect(await overlays.hasHarness(CreateRunDialogHarness)).toBe(false);
  });

  it('keeps narrow viewports read-only rather than leaving a hidden actionable trigger', async () => {
    state.canMutate.set(false);
    const { page, overlays } = await render();
    expect(await page.hasTrigger()).toBe(false);
    expect(await overlays.hasHarness(CreateRunDialogHarness)).toBe(false);
  });
});

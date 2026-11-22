import { InteractivityChecker } from '@angular/cdk/a11y';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { GeneratedScriptsPage } from './generated-scripts.page';
import { generatedScriptsFixture } from './generated-scripts.fixture';
class ScreenHarness extends ComponentHarness {
  static hostSelector = 'app-generated-scripts-review';
  readonly headings = this.locatorForAll('h1');
  readonly text = async () => (await this.host()).text();
  private readonly overlay = this.documentRootLocatorFactory();
  readonly caption = this.locatorFor('table caption');
  readonly dialogs = this.overlay.locatorForAll('[role="dialog"]');
  readonly dialogTitle = this.overlay.locatorFor('[role="dialog"] [hlmDialogTitle]');
  readonly firstClose = this.overlay.locatorFor('[role="dialog"] header button');
  readonly trapEnds = this.overlay.locatorForAll('.cdk-focus-trap-anchor');
  readonly statusBadge = this.overlay.locatorFor('[role="dialog"] app-status-pill [hlmBadge]');
  readonly close = this.overlay.locatorFor('[role="dialog"] footer button');
  readonly code = this.overlay.locatorFor('[role="dialog"] pre code');
  readonly signIn = this.locatorFor('a[routerLink="/login"]');
  readonly injected = this.overlay.locatorForOptional('[role="dialog"] pre strong');
  readonly controls = this.overlay.locatorForAll('[role="dialog"] [data-code-action]');
  readonly statuses = this.locatorForAll('tbody app-status-pill');
  readonly icons = this.locatorForAll('tbody app-status-pill hlm-icon');
  readonly views = this.locatorForAll('tbody button[aria-haspopup="dialog"]');
  readonly filters = this.locatorForAll('[role="group"] button[aria-pressed]');
  readonly footerCount = this.locatorFor('footer [role="status"]');
  private readonly input = this.locatorFor('input[type=search]');
  readonly dialog = this.documentRootLocatorFactory().locatorFor('[role="dialog"]');
  readonly titles = this.locatorForAll('tbody strong bdi');
  readonly automated = this.locatorForAll('tbody tr > td:nth-child(3) bdi');
  readonly detailRegion = this.overlay.locatorFor('[role="dialog"] [role="region"]');
  readonly detailValues = this.overlay.locatorForAll('[role="dialog"] bdi');
  readonly detailTables = this.overlay.locatorForAll('[role="dialog"] table');
  readonly detailRows = this.overlay.locatorForAll('[role="dialog"] tbody tr');

  async searchFor(term: string): Promise<void> {
    const input = await this.input();
    await input.setInputValue(term);
    await input.dispatchEvent('input');
  }
}

describe('GeneratedScriptsPage', () => {
  provideLayoutTestStubs();
  const params = new BehaviorSubject(convertToParamMap({}));
  beforeEach(() => {
    params.next(convertToParamMap({}));
    TestBed.configureTestingModule({
      imports: [GeneratedScriptsPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: params } },
      ],
    });
  });
  async function mount(state = 'ready') {
    params.next(convertToParamMap({ state }));
    const fixture = TestBed.createComponent(GeneratedScriptsPage);
    fixture.detectChanges();
    const screen = await TestbedHarnessEnvironment.harnessForFixture(fixture, ScreenHarness);
    return { fixture, screen };
  }
  it('returns to the selected script and filtered view after sign-in without replaying the simulated expiry', async () => {
    const router = TestBed.inject(Router);
    router.resetConfig([{ path: 'dev/generated-scripts', children: [] }]);
    await router.navigateByUrl(
      '/dev/generated-scripts?generated=gs-0051&state=401&revision=v2&filter=a&filter=b#source',
    );
    const { screen } = await mount('401');
    const href = await (await screen.signIn()).getProperty<string>('href');
    expect(new URL(href).searchParams.get('returnUrl')).toBe(
      '/dev/generated-scripts?generated=gs-0051&revision=v2&filter=a&filter=b#source',
    );
  });
  it.each([
    ['loading', 'Loading'],
    ['empty', 'No generated scripts in this project'],
    ['error', 'Generated scripts could not be loaded'],
    ['403', 'You do not have access to generated scripts'],
    ['404', 'Script not found'],
    ['401', 'Your session expired'],
    ['429', 'Too many requests'],
    ['412', 'The script changed while you were working'],
    ['nomatch', 'No matching scripts'],
    ['mixed', 'Mixed builds'],
    ['reject', 'Lint rejected'],
    ['nolint', 'Lint unavailable'],
    ['seed', 'Generation halted · missing seed'],
    ['score', 'Score unavailable'],
    ['closed', 'Closed PR specimen'],
    ['denied', 'You do not have access to generated scripts'],
    ['success', 'Script inventory'],
    ['unavailable', 'Requested record unavailable'],
  ])('presents the %s review state', async (state, heading) => {
    const { screen } = await mount(state);
    expect(await screen.headings()).toHaveLength(1);
    expect(await screen.text()).toContain(heading);
  });
  it('renders the inventory and script source as inert text with unavailable export actions', async () => {
    const { fixture, screen } = await mount();
    expect(await screen.views()).toHaveLength(3);
    expect(await screen.statuses()).toHaveLength(3);
    expect(await screen.icons()).toHaveLength(3);
    const text = await screen.text();
    for (const id of ['gs-0042', 'gs-0043', 'gs-0051', 'SHOP-TC-0042', 'v4', 'SHOP-TC-0051', 'v2'])
      expect(text).toContain(id);
    const body = '// <strong>Read as source</strong>';
    fixture.componentRef.setInput('viewModel', {
      ...generatedScriptsFixture,
      scripts: generatedScriptsFixture.scripts.map((row) => ({ ...row, source: body })),
    });
    const [view] = await screen.views();
    if (!view) throw new Error('Missing script View button');
    await view.click();
    expect(await (await screen.code()).text()).toContain(body);
    expect(await screen.injected()).toBeNull();
    const actions = await screen.controls();
    expect(actions).toHaveLength(2);
    for (const action of actions) expect(await action.getProperty('disabled')).toBe(true);
  });
  it('escapes format controls and isolates script labels in the list and details', async () => {
    const { fixture, screen } = await mount();
    fixture.componentRef.setInput('viewModel', {
      ...generatedScriptsFixture,
      annotationKey: 'annotation\u200Bkey',
      scripts: generatedScriptsFixture.scripts.map((row) => ({
        ...row,
        title: 'Script\u202Ename',
        automated: 'case\u2066id',
      })),
    });
    const [title] = await screen.titles();
    const [automated] = await screen.automated();
    const [view] = await screen.views();
    if (!title || !automated || !view) throw new Error('Missing script labels');
    expect(await title.text()).toBe('Script⟨U+202E⟩name');
    expect(await automated.text()).toBe('case⟨U+2066⟩id');
    expect(await view.getAttribute('aria-label')).toContain('Script⟨U+202E⟩name');
    expect(await view.hasClass('whitespace-nowrap')).toBe(true);
    expect(await view.hasClass('shrink-0')).toBe(true);
    await view.click();
    const region = await screen.detailRegion();
    expect(await region.getAttribute('aria-label')).toBe('Script⟨U+202E⟩name');
    expect(await region.hasClass('auto-rows-max')).toBe(true);
    const values = await Promise.all((await screen.detailValues()).map((value) => value.text()));
    expect(values).toEqual(
      expect.arrayContaining(['Script⟨U+202E⟩name', 'case⟨U+2066⟩id', 'annotation⟨U+200B⟩key']),
    );
    expect(await screen.detailTables()).toHaveLength(3);
    const row = generatedScriptsFixture.scripts[0];
    if (!row) throw new Error('Missing fixture script');
    expect(await screen.detailRows()).toHaveLength(row.steps.length * 2 + row.attempts.length);
  });
  it('filters locally and renders an honest zero-match instead of an empty project', async () => {
    const { screen } = await mount();
    await screen.searchFor('SHOP-TC-0051');
    expect(await screen.views()).toHaveLength(1);
    await screen.searchFor('not-a-script');
    expect(await screen.views()).toHaveLength(0);
    expect(await screen.text()).toContain('No matching scripts');
  });
  it('keeps the original flaky failure and the unrecorded third attempt alongside the green retry', async () => {
    const { screen } = await mount();
    await screen.searchFor('gs-0051');
    const [trigger] = await screen.views();
    if (!trigger) throw new Error('Missing filtered script trigger');
    expect(await trigger.text()).toBe('View');
    expect(await trigger.getAttribute('aria-label')).toContain('gs-0051');
    await trigger.click();
    const details = await (await screen.dialog()).text();
    expect(details).toContain('Expected total; received stale subtotal.');
    expect(details).toContain('No assertion failure on retry.');
    expect(details).toContain('Original attempt detail not supplied.');
  });
  it('drops script records and source on a reactive read denial', async () => {
    const { screen } = await mount();
    expect(await screen.text()).toContain('gs-0042');
    const [view] = await screen.views();
    if (!view) throw new Error('Missing script View button');
    await view.click();
    expect(await screen.dialogs()).toHaveLength(1);
    params.next(convertToParamMap({ state: 'denied' }));
    expect(await screen.text()).not.toContain('gs-0042');
    expect(await screen.controls()).toHaveLength(0);
    expect(await screen.dialogs()).toHaveLength(0);
  });
  it('does not substitute another script when the requested case and revision disagree', async () => {
    const { screen } = await mount();
    params.next(convertToParamMap({ generated: 'gs-0042', revision: 'v2' }));
    expect(await screen.text()).toContain('Requested record unavailable');
    expect(await screen.views()).toHaveLength(0);
  });
  it.each(['empty', 'loading'])(
    'retains a named inventory without records while %s',
    async (state) => {
      const { screen } = await mount(state);
      expect(await (await screen.caption()).text()).toBe('Script inventory');
      expect(await screen.views()).toHaveLength(0);
      expect(await screen.dialogs()).toHaveLength(0);
      expect(await screen.text()).not.toContain('gs-0042');
    },
  );
  it('cycles focus inside one modal trap at both Tab boundaries', async () => {
    const { screen } = await mount();
    // jsdom supplies no layout; leave the real focus trap and tabbability checks in place.
    const geometry = vi
      .spyOn(TestBed.inject(InteractivityChecker), 'isVisible')
      .mockReturnValue(true);
    try {
      const [view] = await screen.views();
      if (!view) throw new Error('Missing script View button');
      await view.click();
      expect(await (await screen.dialog()).getAttribute('aria-modal')).toBe('true');
      const ends = await screen.trapEnds();
      expect(ends).toHaveLength(2);
      const [start, end] = ends;
      if (!start || !end) throw new Error('Missing focus trap boundaries');
      await end.focus();
      expect(await (await screen.firstClose()).isFocused()).toBe(true);
      await start.focus();
      expect(await (await screen.close()).isFocused()).toBe(true);
    } finally {
      geometry.mockRestore();
    }
  });
  it('uses loaded counts and the existing status filter without introducing a new page of results', async () => {
    const { screen } = await mount();
    const [all, active] = await screen.filters();
    if (!all || !active) throw new Error('Missing script status filters');
    expect(await all.text()).toMatch(/All statuses\s*3/);
    expect(await active.text()).toMatch(/Active\s*1/);
    await active.click();
    expect(await active.getAttribute('aria-pressed')).toBe('true');
    expect(await screen.views()).toHaveLength(1);
    expect(await (await screen.footerCount()).text()).toContain('Showing 1 of 3');
    await all.click();
    expect(await screen.views()).toHaveLength(3);
  });
  it('keeps the mixed-build record neutral in the viewer rather than reverting to its base status', async () => {
    const { screen } = await mount('mixed');
    const [view] = await screen.views();
    if (!view) throw new Error('Missing mixed-build script');
    await view.click();
    const details = await (await screen.dialog()).text();
    expect(await (await screen.statusBadge()).hasClass('status-pill--neutral')).toBe(true);
    expect(details).toContain('Mixed builds');
    expect(details).toContain('1 green on b-317; 2 green on b-318');
    expect(details).not.toContain('3/3 green · b-318');
  });
  it('closes a removed record without substituting another and focuses the nearest remaining View', async () => {
    const { screen, fixture } = await mount();
    const [, removed, next] = await screen.views();
    if (!removed || !next) throw new Error('Missing script View buttons');
    await removed.click();
    expect(await (await screen.dialog()).text()).toContain('gs-0043');
    fixture.componentRef.setInput('viewModel', {
      ...generatedScriptsFixture,
      scripts: generatedScriptsFixture.scripts.filter((row) => row.id !== 'gs-0043'),
    });
    expect(await screen.dialogs()).toHaveLength(0);
    expect(await next.isFocused()).toBe(true);
  });
  it('opens a labelled script dialog and returns focus to the exact View button on Escape or Close', async () => {
    const { screen } = await mount();
    expect(await (await screen.caption()).text()).toBe('Script inventory');
    expect(await screen.dialogs()).toHaveLength(0);
    const [, view] = await screen.views();
    if (!view) throw new Error('Missing second script View button');
    for (const exit of ['Escape', 'Close']) {
      await view.focus();
      await view.click();
      expect(await screen.dialogs()).toHaveLength(1);
      const dialog = await screen.dialog();
      const title = await screen.dialogTitle();
      expect(await title.text()).toBe('Script details');
      expect(await dialog.getAttribute('aria-labelledby')).toBe(await title.getAttribute('id'));
      expect(await title.isFocused()).toBe(true);
      expect(await dialog.text()).toContain('gs-0043');
      expect(await dialog.text()).not.toContain('gs-0042');
      if (exit === 'Escape') await title.sendKeys(TestKey.ESCAPE);
      else await (await screen.close()).click();
      expect(await screen.dialogs()).toHaveLength(0);
      expect(await view.isFocused()).toBe(true);
    }
  });
  it('opens only the pinned record supplied by an existing link and closes when it disappears', async () => {
    const { screen, fixture } = await mount();
    params.next(convertToParamMap({ generated: 'gs-0051', revision: 'v2' }));
    expect(await (await screen.dialog()).text()).toContain('gs-0051');
    fixture.componentRef.setInput('viewModel', {
      ...generatedScriptsFixture,
      scripts: generatedScriptsFixture.scripts.filter((row) => row.id !== 'gs-0051'),
    });
    expect(await screen.dialogs()).toHaveLength(0);
    expect(await screen.text()).toContain('Requested record unavailable');
  });
});

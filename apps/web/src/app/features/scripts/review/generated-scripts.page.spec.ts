import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { GeneratedScriptsPage } from './generated-scripts.page';
import { generatedScriptsFixture } from './generated-scripts.fixture';
class ScreenHarness extends ComponentHarness {
  static hostSelector = 'app-generated-scripts-review';
  readonly headings = this.locatorForAll('h1');
  readonly text = async () => (await this.host()).text();
  readonly code = this.locatorFor('pre code');
  readonly injected = this.locatorForOptional('pre strong');
  readonly controls = this.locatorForAll('[data-code-action]');
  readonly statuses = this.locatorForAll('tbody app-status-pill');
  readonly icons = this.locatorForAll('tbody app-status-pill hlm-icon');
  readonly ids = this.locatorForAll('tbody button[hlmDialogTrigger]');
  private readonly input = this.locatorFor('input[type=search]');
  readonly dialog = this.documentRootLocatorFactory().locatorFor('[role="dialog"]');

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
    expect(await screen.ids()).toHaveLength(3);
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
    expect(await (await screen.code()).text()).toContain(body);
    expect(await screen.injected()).toBeNull();
    const actions = await screen.controls();
    expect(actions).toHaveLength(2);
    for (const action of actions) expect(await action.getProperty('disabled')).toBe(true);
  });
  it('filters locally and renders an honest zero-match instead of an empty project', async () => {
    const { screen } = await mount();
    await screen.searchFor('SHOP-TC-0051');
    expect(await screen.ids()).toHaveLength(1);
    await screen.searchFor('not-a-script');
    expect(await screen.ids()).toHaveLength(0);
    expect(await screen.text()).toContain('No matching scripts');
  });
  it('keeps the original flaky failure and the unrecorded third attempt alongside the green retry', async () => {
    const { screen } = await mount();
    await screen.searchFor('gs-0051');
    const [trigger] = await screen.ids();
    if (!trigger) throw new Error('Missing filtered script trigger');
    expect(await trigger.text()).toBe('gs-0051');
    await trigger.click();
    const details = await (await screen.dialog()).text();
    expect(details).toContain('Expected total; received stale subtotal.');
    expect(details).toContain('No assertion failure on retry.');
    expect(details).toContain('Original attempt detail not supplied.');
  });
  it('drops script records and source on a reactive read denial', async () => {
    const { screen } = await mount();
    expect(await screen.text()).toContain('gs-0042');
    params.next(convertToParamMap({ state: 'denied' }));
    expect(await screen.text()).not.toContain('gs-0042');
    expect(await screen.controls()).toHaveLength(0);
  });
  it('does not substitute another script when the requested case and revision disagree', async () => {
    const { screen } = await mount();
    params.next(convertToParamMap({ generated: 'gs-0042', revision: 'v2' }));
    expect(await screen.text()).toContain('Requested record unavailable');
    expect(await screen.ids()).toHaveLength(0);
  });
});

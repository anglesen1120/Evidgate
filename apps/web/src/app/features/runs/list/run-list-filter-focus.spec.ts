import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { pageStore, runCreatePageProviders } from '../actions/create/create-run-list.test-support';
import { RunListPage } from './run-list.page';
import { RunListStore } from './run-list.store';

class FilterHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly disclosure = this.locatorFor('button[aria-controls="run-filter-panel"]');
  readonly panel = this.documentRootLocatorFactory().locatorForOptional('#run-filter-panel');
  readonly state = this.documentRootLocatorFactory().locatorFor('#run-state');
  readonly all = this.locatorFor('[data-run-quick="all"]');
  readonly fail = this.locatorFor('[data-run-quick="fail"]');
  readonly ref = this.locatorFor('#run-ref');
  readonly apply = this.locatorFor('[data-filter-search]');
  readonly clear = this.locatorFor('[data-filter-clear]');
}

describe('Run list filters', () => {
  provideLayoutTestStubs();

  async function render() {
    const desktop = new BehaviorSubject({ matches: false, breakpoints: {} });
    const store = pageStore();
    await TestBed.configureTestingModule({
      imports: [RunListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        ...runCreatePageProviders().providers,
        {
          provide: BreakpointObserver,
          useValue: { observe: () => desktop.asObservable() },
        },
      ],
    })
      .overrideComponent(RunListPage, {
        set: { providers: [{ provide: RunListStore, useValue: store }] },
      })
      .compileComponents();
    const fixture = TestBed.createComponent(RunListPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, FilterHarness);
    return { store, harness, desktop };
  }

  it('opens secondary filters at both widths and counts only their committed values', async () => {
    const { store, harness, desktop } = await render();
    const toggle = await harness.disclosure();
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await toggle.text()).toContain('Filters (0)');
    await toggle.click();
    expect(await harness.panel()).not.toBeNull();
    await (await harness.ref()).setInputValue('draft-only');
    expect(await toggle.text()).toContain('Filters (0)');
    store.filters.update((filters) => ({ ...filters, decision: 'PASS', refName: 'main' }));
    expect(await toggle.text()).toContain('Filters (1)');
    await toggle.click();
    desktop.next({ matches: true, breakpoints: {} });
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await harness.panel()).toBeNull();
  });

  it('moves focus to the disclosure when collapsing with a focused filter inside', async () => {
    const { harness } = await render();
    const toggle = await harness.disclosure();
    await toggle.click();
    const ref = await harness.state();
    await ref.focus();
    expect(await ref.isFocused()).toBe(true);
    await toggle.click();
    expect(await harness.panel()).toBeNull();
    expect(await toggle.isFocused()).toBe(true);
  });

  it('derives quick-chip selection from committed filters and never submits draft text on a chip click', async () => {
    const { store, harness } = await render();
    expect(await (await harness.all()).getAttribute('aria-pressed')).toBe('true');
    await (await harness.ref()).setInputValue('draft-only');
    await (await harness.fail()).click();
    expect(store.setFilters).toHaveBeenCalledWith({
      state: '',
      scope: '',
      refName: '',
      decision: 'FAIL',
    });
    store.filters.update((filters) => ({ ...filters, decision: 'FAIL' }));
    expect(await (await harness.fail()).getAttribute('aria-pressed')).toBe('true');
    expect(await (await harness.all()).getAttribute('aria-pressed')).toBe('false');
  });

  it('All clears only the decision filter and keeps the other committed filters', async () => {
    const { store, harness } = await render();
    store.filters.update((filters) => ({ ...filters, decision: 'FAIL', refName: 'main' }));
    await (await harness.all()).click();
    expect(store.setFilters).toHaveBeenLastCalledWith({
      state: '',
      scope: '',
      refName: 'main',
      decision: '',
    });
  });

  it.each(['apply', 'clear'] as const)(
    'retains %s focus and refuses repeated intents while busy',
    async (action) => {
      const { store, harness } = await render();
      store.filters.update((filters) => ({ ...filters, decision: 'FAIL' }));
      const button = await harness[action]();
      await button.focus();
      store.busy.set(true);
      expect(await button.isFocused()).toBe(true);
      expect(await button.getProperty('disabled')).toBe(false);
      expect(await button.getAttribute('aria-disabled')).toBe('true');
      expect(await button.getAttribute('aria-busy')).toBe('true');
      await button.click();
      expect(store.setFilters).not.toHaveBeenCalled();
      store.busy.set(false);
      expect(await button.isFocused()).toBe(true);
      expect(await button.getAttribute('aria-disabled')).toBe('false');
      await button.click();
      expect(store.setFilters).toHaveBeenCalledTimes(1);
    },
  );
});

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
  readonly disclosure = this.locatorFor('[data-run-filter-disclosure]');
  readonly panel = this.locatorFor('#run-filter-panel');
  readonly ref = this.locatorFor('#run-ref');
  readonly apply = this.locatorFor('[data-run-apply]');
  readonly clear = this.locatorFor('[data-run-clear]');
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

  it('collapses the form at both widths and counts committed filters rather than drafts', async () => {
    const { store, harness, desktop } = await render();
    const toggle = await harness.disclosure();
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await toggle.text()).toContain('Active filters: 0');
    await toggle.click();
    expect(await (await harness.panel()).getProperty('hidden')).toBe(false);
    await (await harness.ref()).setInputValue('draft-only');
    expect(await toggle.text()).toContain('Active filters: 0');
    store.filters.update((filters) => ({ ...filters, decision: 'PASS', refName: 'main' }));
    expect(await toggle.text()).toContain('Active filters: 2');
    await toggle.click();
    desktop.next({ matches: true, breakpoints: {} });
    expect(await toggle.getAttribute('aria-expanded')).toBe('false');
    expect(await (await harness.panel()).getProperty('hidden')).toBe(true);
  });

  it('moves focus to the disclosure when collapsing with a focused filter inside', async () => {
    const { harness } = await render();
    const toggle = await harness.disclosure();
    await toggle.click();
    const ref = await harness.ref();
    await ref.focus();
    expect(await ref.isFocused()).toBe(true);
    await toggle.click();
    expect(await (await harness.panel()).getProperty('hidden')).toBe(true);
    expect(await toggle.isFocused()).toBe(true);
  });

  it.each(['apply', 'clear'] as const)(
    'retains %s focus and refuses repeated intents while busy',
    async (action) => {
      const { store, harness } = await render();
      await (await harness.disclosure()).click();
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

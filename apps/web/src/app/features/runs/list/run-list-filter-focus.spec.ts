import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { pageStore, runCreatePageProviders } from '../actions/create/create-run-list.test-support';
import { RunListPage } from './run-list.page';
import { RunListStore } from './run-list.store';

class FilterHarness extends ComponentHarness {
  static hostSelector = 'app-run-list-page';
  readonly apply = this.locatorFor('[data-run-apply]');
  readonly clear = this.locatorFor('[data-run-clear]');
}

describe('Run list filter focus during retained-row reads', () => {
  provideLayoutTestStubs();

  it.each(['apply', 'clear'] as const)(
    'retains %s focus and refuses repeated intents while busy',
    async (action) => {
      const store = pageStore();
      await TestBed.configureTestingModule({
        imports: [RunListPage],
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          ...runCreatePageProviders().providers,
          {
            provide: BreakpointObserver,
            useValue: { observe: () => of({ matches: true, breakpoints: {} }) },
          },
        ],
      })
        .overrideComponent(RunListPage, {
          set: { providers: [{ provide: RunListStore, useValue: store }] },
        })
        .compileComponents();
      const fixture = TestBed.createComponent(RunListPage);
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, FilterHarness);
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

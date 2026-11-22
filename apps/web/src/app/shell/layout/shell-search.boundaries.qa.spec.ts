import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NAV_ENTRIES } from '../nav/nav-registry';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellSearchComponent } from './shell-search.component';

provideLayoutTestStubs();

class SearchBoundaryTrigger extends ComponentHarness {
  static hostSelector = 'app-shell-search';
  readonly button = this.locatorFor('button');
}

class SearchBoundaryPicker extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly input = this.locatorFor('input[role="combobox"]');
  readonly options = this.locatorForAll('[role="option"]');
  readonly empty = this.locatorForOptional('[role="status"]');
}

describe('Shell screen search boundary interactions', () => {
  async function open(grantedIds = ['overview', 'authoring', 'ai-settings']) {
    const permissions = new Set<string>(
      NAV_ENTRIES.filter((entry) => grantedIds.includes(entry.id)).map((entry) => entry.permission),
    );
    TestBed.configureTestingModule({
      imports: [ShellSearchComponent],
      providers: [
        provideRouter([{ path: '**', component: NotFoundPage }]),
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'o' }),
            currentProject: signal({ id: 'p' }),
            has: (code: string) => permissions.has(code),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ShellSearchComponent);
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const trigger = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      SearchBoundaryTrigger,
    );
    const button = await trigger.button();
    await button.focus();
    expect(await button.isFocused()).toBe(true);
    await button.click();
    const picker = await loader.getHarness(SearchBoundaryPicker);
    return { picker, trigger, loader };
  }

  it('keeps an empty-permission search open on arrows and Enter without navigating', async () => {
    const { picker, loader } = await open([]);
    const input = await picker.input();
    expect(await picker.options()).toHaveLength(0);
    expect(await (await picker.empty())?.text()).toBe('No matching screens.');
    await input.sendKeys(TestKey.DOWN_ARROW, TestKey.UP_ARROW, TestKey.ENTER);
    expect(await input.getAttribute('aria-activedescendant')).toBeNull();
    expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(true);
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('shows every permitted destination for empty and whitespace-only queries', async () => {
    const { picker } = await open();
    const expected = ['Overview', 'Feature tree', 'Project AI settings'];
    expect(await Promise.all((await picker.options()).map((option) => option.text()))).toEqual(
      expected,
    );
    const input = await picker.input();
    await input.setInputValue(' \t ');
    await input.dispatchEvent('input');
    expect(await Promise.all((await picker.options()).map((option) => option.text()))).toEqual(
      expected,
    );
    expect(await picker.empty()).toBeNull();
  });

  it('wraps from the first result to the last and back with the arrow keys', async () => {
    const { picker } = await open();
    const input = await picker.input();
    const options = await picker.options();
    expect(options.length).toBeGreaterThan(1);
    await input.sendKeys(TestKey.DOWN_ARROW, TestKey.UP_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).toBe(
      await options.at(-1)?.getAttribute('id'),
    );
    await input.sendKeys(TestKey.DOWN_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).toBe(
      await options[0]?.getAttribute('id'),
    );
  });

  it('clears a stale active descendant when a query has no matches', async () => {
    const { picker } = await open();
    const input = await picker.input();
    await input.sendKeys(TestKey.DOWN_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).not.toBeNull();
    await input.setInputValue('no-such-screen');
    await input.dispatchEvent('input');
    expect(await picker.options()).toHaveLength(0);
    expect(await input.getAttribute('aria-activedescendant')).toBeNull();
    await input.sendKeys(TestKey.ENTER);
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('restores opener focus on Escape and resets the query before reopening', async () => {
    const { picker, trigger, loader } = await open();
    const input = await picker.input();
    await input.setInputValue('feature');
    await input.dispatchEvent('input');
    expect(await picker.options()).toHaveLength(1);
    await input.sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(false));
    expect(await (await trigger.button()).isFocused()).toBe(true);
    await (await trigger.button()).click();
    const reopened = await loader.getHarness(SearchBoundaryPicker);
    expect(await (await reopened.input()).getProperty('value')).toBe('');
    expect(await reopened.options()).toHaveLength(3);
    expect(TestBed.inject(Router).url).toBe('/');
  });
});

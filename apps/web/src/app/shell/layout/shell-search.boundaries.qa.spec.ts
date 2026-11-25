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

const NAV_LABELS = ['Overview', 'Feature tree', 'Project AI settings'];

class SearchBoundaryTrigger extends ComponentHarness {
  static hostSelector = 'app-shell-search';
  readonly button = this.locatorFor('button');
}

class SearchBoundaryPicker extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly input = this.locatorFor('input[role="combobox"]');
  readonly options = this.locatorForAll('[role="option"]');
  readonly status = this.locatorFor('[role="status"]');
}

describe('Shell command palette boundaries', () => {
  async function open(grantedIds = ['overview', 'authoring', 'ai-settings']) {
    const granted = new Set(
      NAV_ENTRIES.filter((entry) => grantedIds.includes(entry.id)).map((entry) => entry.permission),
    );
    const permissions = signal(granted);
    TestBed.configureTestingModule({
      imports: [ShellSearchComponent],
      providers: [
        provideRouter([{ path: '**', component: NotFoundPage }]),
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'org' }),
            currentProject: signal({ id: 'project' }),
            has: (code: string) => permissions().has(code),
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
    await (await trigger.button()).focus();
    await (await trigger.button()).click();
    await vi.waitFor(async () => expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(true));
    const picker = await loader.getHarness(SearchBoundaryPicker);
    return { picker, loader, trigger };
  }

  async function optionTexts(picker: SearchBoundaryPicker): Promise<string[]> {
    return Promise.all((await picker.options()).map((option) => option.text()));
  }

  it('offers no destinations without permissions; a non-matching query stays open with no active option', async () => {
    const { picker, loader } = await open([]);
    expect(await optionTexts(picker)).not.toEqual(expect.arrayContaining(NAV_LABELS));
    const input = await picker.input();
    await input.setInputValue('no-such-screen');
    await input.dispatchEvent('input');
    expect(await picker.options()).toHaveLength(0);
    expect(await (await picker.status()).text()).toBe('No results');
    await input.sendKeys(TestKey.DOWN_ARROW, TestKey.UP_ARROW, TestKey.ENTER);
    expect(await input.getAttribute('aria-activedescendant')).toBeNull();
    expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(true);
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it.each(['', '   '])('shows every destination for the query %j', async (query) => {
    const { picker, loader } = await open();
    const input = await picker.input();
    await input.setInputValue(query);
    await input.dispatchEvent('input');
    expect((await optionTexts(picker)).slice(0, NAV_LABELS.length)).toEqual(NAV_LABELS);
    expect(await (await picker.status()).text()).not.toBe('No results');
    await input.sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(false));
  });

  it('wraps the active option from last to first and first to last', async () => {
    const { picker } = await open();
    const input = await picker.input();
    const options = await picker.options();
    await input.sendKeys(TestKey.DOWN_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).toBe(
      await options[0]!.getAttribute('id'),
    );
    await input.sendKeys(TestKey.UP_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).toBe(
      await options[options.length - 1]!.getAttribute('id'),
    );
    await input.sendKeys(TestKey.DOWN_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).toBe(
      await options[0]!.getAttribute('id'),
    );
  });

  it('clears a stale active option when the query matches nothing', async () => {
    const { picker } = await open();
    const input = await picker.input();
    await input.setInputValue('overview');
    await input.dispatchEvent('input');
    await input.sendKeys(TestKey.DOWN_ARROW);
    expect(await input.getAttribute('aria-activedescendant')).not.toBeNull();

    await input.setInputValue('no-such-screen');
    await input.dispatchEvent('input');
    expect(await input.getAttribute('aria-activedescendant')).toBeNull();
    await input.sendKeys(TestKey.ENTER);
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('returns focus to the trigger on Escape and reopens with an empty query', async () => {
    const { picker, loader, trigger } = await open();
    const input = await picker.input();
    await input.setInputValue('feature');
    await input.dispatchEvent('input');
    expect(await optionTexts(picker)).toEqual(['Feature tree']);

    await input.sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(false));
    await vi.waitFor(() =>
      expect(document.activeElement).toBe(document.querySelector('button.screen-search')),
    );
    expect(TestBed.inject(Router).url).toBe('/');

    await (await trigger.button()).click();
    await vi.waitFor(async () => expect(await loader.hasHarness(SearchBoundaryPicker)).toBe(true));
    const reopened = await loader.getHarness(SearchBoundaryPicker);
    expect(await (await reopened.input()).getProperty('value')).toBe('');
    expect((await optionTexts(reopened)).slice(0, NAV_LABELS.length)).toEqual(NAV_LABELS);
  });
});

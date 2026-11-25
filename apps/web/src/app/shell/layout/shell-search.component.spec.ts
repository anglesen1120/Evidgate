import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ErrorHandler, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { SessionStore } from '../../core/session/session.store';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellSearchComponent } from './shell-search.component';

provideLayoutTestStubs();

class SearchTrigger extends ComponentHarness {
  static hostSelector = 'app-shell-search';
  readonly button = this.locatorFor('button');
}

class PalettePicker extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly input = this.locatorFor('input[role="combobox"]');
  readonly options = this.locatorForAll('[role="option"]');
  readonly status = this.locatorFor('[role="status"]');
}

describe('Shell command palette', () => {
  async function open() {
    const permissions = signal(new Set(['project:read', 'authoring:read']));
    TestBed.configureTestingModule({
      imports: [ShellSearchComponent],
      providers: [
        provideRouter([{ path: '**', component: NotFoundPage }]),
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'org' }),
            currentProject: signal({ id: 'project' }),
            organizations: signal([]),
            projects: signal([]),
            has: (code: string) => permissions().has(code),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ShellSearchComponent);
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const trigger = await TestbedHarnessEnvironment.harnessForFixture(fixture, SearchTrigger);
    await (await trigger.button()).click();
    await vi.waitFor(async () => expect(await loader.hasHarness(PalettePicker)).toBe(true));
    const picker = await loader.getHarness(PalettePicker);
    return { picker, permissions, loader, trigger };
  }

  async function optionTexts(picker: PalettePicker): Promise<string[]> {
    return Promise.all((await picker.options()).map((option) => option.text()));
  }

  it('filters destinations and drops a result when its permission is lost', async () => {
    const { picker, permissions } = await open();
    const input = await picker.input();
    await input.setInputValue('FEATURE');
    await input.dispatchEvent('input');
    expect(await optionTexts(picker)).toEqual(['Feature tree']);

    permissions.set(new Set(['project:read']));
    expect(await picker.options()).toHaveLength(0);
    expect(await (await picker.status()).text()).toBe('No results');
    await input.sendKeys(TestKey.ENTER);
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('moves the active option with arrows and navigates to its scoped URL on Enter', async () => {
    const { picker } = await open();
    const input = await picker.input();
    await input.sendKeys(TestKey.DOWN_ARROW, TestKey.DOWN_ARROW, TestKey.UP_ARROW);
    const options = await picker.options();
    expect(await input.getAttribute('aria-activedescendant')).toBe(
      await options[0]!.getAttribute('id'),
    );
    await input.sendKeys(TestKey.ENTER);
    await vi.waitFor(() =>
      expect(TestBed.inject(Router).url).toBe('/orgs/org/projects/project/overview'),
    );
  });

  it('does not commit the highlighted command while Enter is composing text', async () => {
    const { picker, loader } = await open();
    const input = await picker.input();
    await input.sendKeys(TestKey.DOWN_ARROW);
    await input.dispatchEvent('keydown', { key: 'Enter', isComposing: true });
    expect(await loader.hasHarness(PalettePicker)).toBe(true);
    expect(TestBed.inject(Router).url).toBe('/');
    await input.sendKeys(TestKey.ENTER);
    await vi.waitFor(() =>
      expect(TestBed.inject(Router).url).toBe('/orgs/org/projects/project/overview'),
    );
  });

  it('ignores a shortcut event with no key', async () => {
    const { picker, trigger, loader } = await open();
    await (await picker.input()).sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await loader.hasHarness(PalettePicker)).toBe(false));
    const errors = vi.spyOn(TestBed.inject(ErrorHandler), 'handleError');
    await (await trigger.button()).dispatchEvent('keydown', { ctrlKey: true });
    expect(errors).not.toHaveBeenCalled();
    expect(await loader.hasHarness(PalettePicker)).toBe(false);
  });

  it('dismisses the palette on Escape without navigating', async () => {
    const { picker, loader } = await open();
    await (await picker.input()).sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await loader.hasHarness(PalettePicker)).toBe(false));
    expect(TestBed.inject(Router).url).toBe('/');
  });
});

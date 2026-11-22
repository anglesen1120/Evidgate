import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { HlmTabsImports } from './index';

@Component({
  selector: 'app-tab-strip-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmTabsImports],
  template: `
    <hlm-tabs [tab]="selected()" [activationMode]="'manual'">
      <hlm-tabs-list [attr.aria-label]="label">
        @for (key of keys; track key) {
          <button [hlmTabsTrigger]="key">{{ key }}</button>
        }
      </hlm-tabs-list>
      @for (key of keys; track key) {
        <section [hlmTabsContent]="key">{{ key }}</section>
      }
    </hlm-tabs>
  `,
})
class TabStripHost {
  readonly keys = ['Evidence', 'Lịch sử thay đổi và các bằng chứng liên quan', 'Decision'];
  readonly label = 'Record sections';
  readonly selected = signal('Evidence');
}

class TabStripHarness extends ComponentHarness {
  static hostSelector = 'app-tab-strip-host';
  readonly root = this.locatorFor('hlm-tabs');
  readonly list = this.locatorFor('[role="tablist"]');
  private readonly triggers = this.locatorForAll('[role="tab"]');

  async tabs() {
    const [first, middle, last] = await this.triggers();
    if (!first || !middle || !last) throw new Error('Expected three tabs');
    return [first, middle, last] as const;
  }
}

async function setup() {
  const fixture = TestBed.createComponent(TabStripHost);
  const strip = await TestbedHarnessEnvironment.harnessForFixture(fixture, TabStripHarness);
  return { fixture, strip };
}

describe('horizontal Helm tab strip', () => {
  provideLayoutTestStubs();

  it('keeps the strip horizontal on one local scroll row', async () => {
    const { strip } = await setup();
    const list = await strip.list();
    expect(await (await strip.root()).getAttribute('data-orientation')).toBe('horizontal');
    expect(await list.getAttribute('aria-orientation')).toBe('horizontal');
    expect(await list.hasClass('flex-nowrap!')).toBe(true);
    expect(await list.hasClass('overflow-x-auto')).toBe(true);
    for (const tab of await strip.tabs()) {
      expect(await tab.getAttribute('data-orientation')).toBe('horizontal');
      expect(await tab.hasClass('flex-none!')).toBe(true);
      expect(await tab.hasClass('whitespace-nowrap!')).toBe(true);
    }
  });

  it('moves focus with Left, Right, Home and End without selecting a manual panel', async () => {
    const { strip } = await setup();
    const [first, middle, last] = await strip.tabs();
    await first.focus();
    await first.sendKeys(TestKey.RIGHT_ARROW);
    expect(await middle.isFocused()).toBe(true);
    await middle.sendKeys(TestKey.END);
    expect(await last.isFocused()).toBe(true);
    await last.sendKeys(TestKey.LEFT_ARROW);
    expect(await middle.isFocused()).toBe(true);
    await middle.sendKeys(TestKey.HOME);
    expect(await first.isFocused()).toBe(true);
    expect(await first.getAttribute('aria-selected')).toBe('true');
  });

  it('reveals focused and programmatically selected tabs only inside an overflowing strip', async () => {
    const { fixture, strip } = await setup();
    const list = TestbedHarnessEnvironment.getNativeElement(await strip.list());
    const [first, middle, last] = await strip.tabs();
    const reveal = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation((options) => {
      // Model an engine that reads the standard container option.
      if (typeof options === 'object') Reflect.get(options, 'container');
    });
    Object.defineProperty(list, 'scrollWidth', { configurable: true, value: 1200 });
    Object.defineProperty(list, 'clientWidth', { configurable: true, value: 900 });
    try {
      await first.focus();
      await first.sendKeys(TestKey.RIGHT_ARROW);
      expect(reveal.mock.contexts.at(-1)).toBe(TestbedHarnessEnvironment.getNativeElement(middle));
      fixture.componentInstance.selected.set('Decision');
      await fixture.whenStable();
      expect(reveal.mock.contexts.at(-1)).toBe(TestbedHarnessEnvironment.getNativeElement(last));
      expect(reveal).toHaveBeenLastCalledWith({
        block: 'nearest',
        inline: 'nearest',
        container: 'nearest',
      });
      expect(await middle.isFocused()).toBe(true);
    } finally {
      reveal.mockRestore();
    }
  });

  it('scrolls the strip, not the page, in engines that ignore the container option', async () => {
    const { strip } = await setup();
    const list = TestbedHarnessEnvironment.getNativeElement(await strip.list());
    const [, , last] = await strip.tabs();
    const target = TestbedHarnessEnvironment.getNativeElement(last);
    Object.defineProperty(list, 'scrollWidth', { configurable: true, value: 1200 });
    Object.defineProperty(list, 'clientWidth', { configurable: true, value: 900 });
    vi.spyOn(list, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 1200, 900, 44));
    vi.spyOn(target, 'getBoundingClientRect').mockReturnValue(new DOMRect(900, 1200, 300, 44));
    const reveal = vi.fn();
    Object.defineProperty(target, 'scrollIntoView', { configurable: true, value: reveal });
    await last.focus();
    expect(list.scrollLeft).toBeGreaterThanOrEqual(300);
    expect(reveal).not.toHaveBeenCalled();
  });
});

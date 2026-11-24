import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../testing/layout.testing';
import { DataTablePagerComponent } from './data-table-pager.component';

class PagerHarness extends ComponentHarness {
  static hostSelector = 'app-data-table-pager';
  readonly nav = this.locatorFor('nav');
  readonly summary = this.locatorFor('[role="status"]');
  readonly label = this.locatorFor('label');
  readonly trigger = this.locatorFor('hlm-select-trigger button');
  readonly previous = this.locatorFor('button[data-page="previous"]');
  readonly next = this.locatorFor('button[data-page="next"]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
}

async function render() {
  const fixture = TestBed.createComponent(DataTablePagerComponent);
  const previous = vi.fn();
  const next = vi.fn();
  const size = vi.fn();
  fixture.componentInstance.previous.subscribe(previous);
  fixture.componentInstance.next.subscribe(next);
  fixture.componentInstance.pageSizeChange.subscribe(size);
  const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, PagerHarness);
  return { fixture, pager, previous, next, size };
}

describe('DataTablePagerComponent', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DataTablePagerComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  it.each([
    { page: 1, total: 53, count: 20, more: true, text: '1–20 of 53', back: false },
    { page: 2, total: 53, count: 20, more: true, text: '21–40 of 53', back: true },
    { page: 2, total: 53, count: 6, more: true, text: '21–26 of 53', back: true },
    { page: 3, total: 53, count: 13, more: false, text: '41–53 of 53', back: true },
    { page: 1, total: 0, count: 0, more: false, text: '0–0 of 0', back: false },
    { page: 1, total: 3, count: 3, more: false, text: '1–3 of 3', back: false },
    { page: 4, total: null, count: 11, more: true, text: 'Page 4', back: true },
    { page: 5, total: null, count: 20, more: false, text: 'Page 5', back: true },
  ])('shows $text and allows only available moves', async (row) => {
    const { page, total, count, more, text, back } = row;
    const { fixture, pager, previous, next } = await render();
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('total', total);
    fixture.componentRef.setInput('count', count);
    fixture.componentRef.setInput('hasNext', more);
    const summary = await pager.summary();
    expect(await summary.text()).toBe(text);
    expect(await summary.getAttribute('aria-live')).toBe('polite');
    expect(await (await pager.nav()).getAttribute('aria-label')).toBe('Table pagination');
    await (await pager.previous()).click();
    await (await pager.next()).click();
    expect(previous).toHaveBeenCalledTimes(back ? 1 : 0);
    expect(next).toHaveBeenCalledTimes(more ? 1 : 0);
  });

  it('blocks repeat activation and keeps focus while busy', async () => {
    const { fixture, pager, next } = await render();
    fixture.componentRef.setInput('hasNext', true);
    const button = await pager.next();
    await button.focus();
    await manualChangeDetection(async () => {
      await Promise.all([button.click(), button.click()]);
    });
    expect(next).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('busy', true);
    const nav = await pager.nav();
    expect(await nav.getAttribute('aria-busy')).toBe('true');
    expect(await button.getAttribute('aria-disabled')).toBe('true');
    await button.click();
    expect(next).toHaveBeenCalledTimes(1);
    expect(await button.isFocused()).toBe(true);
    fixture.componentRef.setInput('busy', false);
    await (await pager.next()).click();
    expect(next).toHaveBeenCalledTimes(2);
  });

  it.each([20, 50, 100])('offers exactly three sizes and emits numeric %i', async (chosen) => {
    const { fixture, pager, size } = await render();
    const trigger = await pager.trigger();
    expect(await trigger.text()).toBe('20');
    expect(await (await pager.label()).getAttribute('for')).toBe(await trigger.getAttribute('id'));
    fixture.componentRef.setInput('pageSize', chosen === 20 ? 50 : 20);
    await (await pager.trigger()).click();
    const options = await pager.options();
    const texts = await Promise.all(options.map((option) => option.text()));
    expect(texts).toEqual(['20', '50', '100']);
    const option = options[texts.indexOf(String(chosen))];
    if (!option) throw new Error('Missing page size');
    await option.click();
    expect(size.mock.calls).toEqual([[chosen]]);
  });

  it('does not reload when the active page size is selected again', async () => {
    const { pager, size } = await render();
    await (await pager.trigger()).click();
    const [current] = await pager.options();
    if (!current) throw new Error('Missing active page size');
    expect(await current.text()).toBe('20');
    await current.click();
    expect(size).not.toHaveBeenCalled();
  });

  it('keeps the size control focusable while busy and refuses changes', async () => {
    const { fixture, pager, size } = await render();
    const trigger = await pager.trigger();
    await trigger.focus();
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    expect(await trigger.isFocused()).toBe(true);
    await trigger.click();
    const options = await pager.options();
    expect(options).toHaveLength(3);
    for (const option of options) {
      expect(await option.getAttribute('aria-disabled')).toBe('true');
      await option.click();
    }
    expect(size).not.toHaveBeenCalled();
  });
});

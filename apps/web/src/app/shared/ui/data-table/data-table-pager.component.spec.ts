import { ComponentHarness, manualChangeDetection, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HlmSelect } from '@evidgate/ui/select';
import { provideLayoutTestStubs } from '../../testing/layout.testing';
import { DataTablePagerComponent } from './data-table-pager.component';

@Component({
  imports: [DataTablePagerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-data-table-pager
      [pageSize]="served()"
      [hasNext]="true"
      [busy]="busy()"
      (pageSizeChange)="request($event)"
    />
  `,
})
class PagerOwner {
  readonly served = signal(20);
  readonly busy = signal(false);
  readonly requests: number[] = [];
  readonly retryTarget = signal<number | null>(null);

  request(size: number): void {
    this.requests.push(size);
    this.retryTarget.set(size);
    this.busy.set(true);
  }

  retry(): void {
    const target = this.retryTarget();
    if (target !== null) this.request(target);
  }
}

class PagerHarness extends ComponentHarness {
  static hostSelector = 'app-data-table-pager';
  readonly nav = this.locatorFor('nav');
  readonly current = this.locatorFor('[role="status"]');
  readonly statuses = this.locatorForAll('[role="status"]');
  readonly controls = this.locatorForAll('button');
  readonly range = this.locatorFor('.page-range');
  readonly label = this.locatorFor('label');
  readonly trigger = this.locatorFor('hlm-select-trigger button');
  readonly previous = this.locatorFor('button[data-page="previous"]');
  readonly next = this.locatorFor('button[data-page="next"]');
  readonly feedback = this.locatorFor('[data-page-feedback]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');

  async sizeOption(size: number) {
    for (const option of await this.options()) {
      if ((await option.text()) === String(size)) return option;
    }
    throw new Error(`Missing page size ${size}`);
  }
}

async function render() {
  const fixture = TestBed.createComponent(DataTablePagerComponent);
  fixture.componentRef.setInput('hasNext', true);
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

  it('keeps a single live page number when the total becomes known', async () => {
    const { fixture, pager } = await render();
    const current = TestbedHarnessEnvironment.getNativeElement(await pager.current());
    for (const total of [null, 60, null]) {
      fixture.componentRef.setInput('total', total);
      const statuses = await pager.statuses();
      expect(statuses).toHaveLength(1);
      expect(TestbedHarnessEnvironment.getNativeElement(statuses[0]!)).toBe(current);
    }
  });

  it.each([
    { count: 0, total: 0, text: '0–0 of 0' },
    { count: 1, total: null, text: '1–1' },
    { count: 3, total: 3, text: '1–3 of 3' },
    { count: 20, total: null, text: '1–20' },
  ])('keeps only the range when $count records fit the smallest size', async (row) => {
    const { fixture, pager } = await render();
    fixture.componentRef.setInput('count', row.count);
    fixture.componentRef.setInput('total', row.total);
    fixture.componentRef.setInput('hasNext', false);
    expect(await pager.controls()).toHaveLength(0);
    expect(await pager.statuses()).toHaveLength(0);
    expect(await (await pager.range()).text()).toBe(`Showing ${row.text}`);
  });

  it('still offers a smaller size when a single page has more than 20 records', async () => {
    const { fixture, pager } = await render();
    fixture.componentRef.setInput('pageSize', 50);
    fixture.componentRef.setInput('count', 21);
    fixture.componentRef.setInput('hasNext', false);
    expect(await (await pager.trigger()).text()).toBe('50');
    expect(await (await pager.next()).getAttribute('aria-disabled')).toBe('true');
  });

  it('keeps the focused control through the fetch, then hands focus to the remaining range', async () => {
    const { fixture, pager } = await render();
    const next = await pager.next();
    await next.focus();
    await next.click();
    fixture.componentRef.setInput('busy', true);
    fixture.componentRef.setInput('hasNext', false);
    expect(await (await pager.feedback()).hasClass('invisible')).toBe(false);
    expect(await next.isFocused()).toBe(true);
    fixture.componentRef.setInput('busy', false);
    expect(await pager.controls()).toHaveLength(0);
    expect(await (await pager.range()).isFocused()).toBe(true);
    expect(await (await pager.range()).getProperty('tabIndex')).toBe(-1);
  });

  it('does not reclaim focus if the user left before the response', async () => {
    const { fixture, pager } = await render();
    const next = await pager.next();
    await next.focus();
    await next.blur();
    const outside = document.activeElement;
    fixture.componentRef.setInput('hasNext', false);
    expect(await pager.controls()).toHaveLength(0);
    expect(document.activeElement).toBe(outside);
  });

  it('reserves local feedback for every pending pager action without adding a live announcer', async () => {
    const { fixture, pager } = await render();
    const feedback = await pager.feedback();
    expect(await feedback.text()).toBe('Loading…');
    expect(await feedback.getAttribute('role')).toBeNull();
    expect(await feedback.hasClass('invisible')).toBe(true);
    for (let read = 0; read < 3; read++) {
      fixture.componentRef.setInput('busy', true);
      expect(await feedback.getAttribute('role')).toBe('progressbar');
      expect(await feedback.getAttribute('aria-label')).toBe('Loading…');
      expect(await feedback.getAttribute('aria-live')).toBeNull();
      expect(await feedback.hasClass('invisible')).toBe(false);
      expect(await (await pager.nav()).getAttribute('aria-busy')).toBe('true');
      fixture.componentRef.setInput('busy', false);
      expect(await feedback.getAttribute('role')).toBeNull();
      expect(await feedback.hasClass('invisible')).toBe(true);
    }
  });

  it('keeps retry controls locked after a settled read without showing loading feedback', async () => {
    const { fixture, pager, next } = await render();
    fixture.componentRef.setInput('hasNext', true);
    fixture.componentRef.setInput('busy', true);
    fixture.componentRef.setInput('fetching', true);
    const feedback = await pager.feedback();
    expect(await feedback.getAttribute('role')).toBe('progressbar');
    fixture.componentRef.setInput('fetching', false);
    expect(await feedback.getAttribute('role')).toBeNull();
    expect(await feedback.hasClass('invisible')).toBe(true);
    expect(await (await pager.nav()).getAttribute('aria-busy')).toBe('false');
    const button = await pager.next();
    expect(await button.getAttribute('aria-busy')).toBeNull();
    expect(await button.getAttribute('aria-disabled')).toBe('true');
    await button.click();
    expect(next).not.toHaveBeenCalled();
  });

  it.each([
    { page: 1, total: 53, count: 20, more: true, text: '1–20 of 53', back: false },
    { page: 2, total: 53, count: 20, more: true, text: '21–40 of 53', back: true },
    { page: 2, total: 53, count: 6, more: true, text: '21–26 of 53', back: true },
    { page: 3, total: 53, count: 13, more: false, text: '41–53 of 53', back: true },
    { page: 1, total: null, count: 1, more: true, text: '1–1', back: false },
    { page: 4, total: null, count: 11, more: true, text: '1–11', back: true },
    { page: 5, total: null, count: 20, more: false, text: '1–20', back: true },
    { page: 2, total: null, count: 0, more: false, text: '0–0', back: true },
  ])('shows $text and allows only available moves', async (row) => {
    const { page, total, count, more, text, back } = row;
    const { fixture, pager, previous, next } = await render();
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('total', total);
    fixture.componentRef.setInput('count', count);
    fixture.componentRef.setInput('hasNext', more);
    expect(await (await pager.range()).text()).toBe(`Showing ${text}`);
    expect(await (await pager.current()).text()).toBe(`Page ${page}`);
    expect(await (await pager.nav()).getAttribute('aria-label')).toBe('Table pagination');
    await (await pager.previous()).click();
    await (await pager.next()).click();
    expect(previous).toHaveBeenCalledTimes(back ? 1 : 0);
    expect(next).toHaveBeenCalledTimes(more ? 1 : 0);
  });

  it('qualifies a loaded-window total and keeps the last window focusable but inert', async () => {
    const { fixture, pager, next } = await render();
    fixture.componentRef.setInput('loadedCaption', 'Audit');
    fixture.componentRef.setInput('pageSize', 200);
    fixture.componentRef.setInput('total', 201);
    fixture.componentRef.setInput('page', 2);
    fixture.componentRef.setInput('count', 1);
    fixture.componentRef.setInput('hasNext', true);
    expect(await (await pager.range()).text()).toBe('Showing 201–201 of 201 loaded records');
    const boundary = await pager.next();
    expect(await boundary.getAttribute('aria-disabled')).toBe('true');
    await boundary.focus();
    await boundary.click();
    expect(await boundary.isFocused()).toBe(true);
    expect(next).not.toHaveBeenCalled();
  });

  it('names both icon buttons for assistive technology', async () => {
    const { pager } = await render();
    expect(await (await pager.previous()).getAttribute('aria-label')).toBe('Previous page');
    expect(await (await pager.next()).getAttribute('aria-label')).toBe('Next page');
  });

  it.each([
    { page: 1, hasNext: true, previousMuted: true, nextMuted: false },
    { page: 2, hasNext: false, previousMuted: false, nextMuted: true },
  ])('keeps server boundaries focusable on page $page with hasNext=$hasNext', async (row) => {
    const { fixture, pager } = await render();
    fixture.componentRef.setInput('page', row.page);
    fixture.componentRef.setInput('hasNext', row.hasNext);
    const actions = [
      [await pager.previous(), row.previousMuted],
      [await pager.next(), row.nextMuted],
    ] as const;
    for (const [button, muted] of actions) {
      expect(await button.hasClass('page-boundary')).toBe(muted);
      expect(await button.getAttribute('aria-disabled')).toBe(String(muted));
      expect(await button.getProperty('disabled')).toBe(false);
      expect(await button.getProperty('tabIndex')).toBe(0);
      await button.focus();
      expect(await button.isFocused()).toBe(true);
    }
  });

  it('blocks repeat activation and keeps focus and boundary styling while busy', async () => {
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
    expect(await (await pager.previous()).hasClass('page-boundary')).toBe(true);
    expect(await button.hasClass('page-boundary')).toBe(false);
    for (const action of [await pager.previous(), button]) {
      expect(await action.getAttribute('aria-busy')).toBeNull();
      expect(await action.getAttribute('aria-disabled')).toBe('true');
      expect(await action.getProperty('disabled')).toBe(false);
      expect(await action.getProperty('tabIndex')).toBe(0);
    }
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

  it('restores the served size after failure and accepts the same request on retry', async () => {
    const fixture = TestBed.createComponent(PagerOwner);
    const owner = fixture.componentInstance;
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(PagerHarness);
    const trigger = await pager.trigger();
    expect(await trigger.text()).toBe('20');
    await trigger.click();
    await (await pager.sizeOption(50)).click();
    expect(owner.requests).toEqual([50]);
    expect(await (await pager.nav()).getAttribute('aria-busy')).toBe('true');

    owner.busy.set(false);
    await fixture.whenStable();
    expect(await trigger.text()).toBe('20');
    await trigger.click();
    expect(await (await pager.sizeOption(20)).getAttribute('aria-selected')).toBe('true');
    const retry = await pager.sizeOption(50);
    await manualChangeDetection(async () => {
      await Promise.all([retry.click(), retry.click()]);
    });
    expect(owner.requests).toEqual([50, 50]);

    owner.served.set(50);
    owner.busy.set(false);
    await fixture.whenStable();
    expect(await trigger.text()).toBe('50');
    await trigger.click();
    const committed = await pager.sizeOption(50);
    expect(await committed.getAttribute('aria-selected')).toBe('true');
    await committed.click();
    expect(owner.requests).toEqual([50, 50]);
  });

  it('leaves the failed target with the owner when the owner retries without reselecting', async () => {
    const fixture = TestBed.createComponent(PagerOwner);
    const owner = fixture.componentInstance;
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(PagerHarness);
    const trigger = await pager.trigger();
    await trigger.click();
    await (await pager.sizeOption(50)).click();
    owner.busy.set(false);
    await fixture.whenStable();
    expect(await trigger.text()).toBe('20');
    expect(owner.retryTarget()).toBe(50);
    owner.retry();
    await fixture.whenStable();
    expect(owner.requests).toEqual([50, 50]);
    expect(await trigger.text()).toBe('20');
    owner.served.set(50);
    owner.busy.set(false);
    await fixture.whenStable();
    expect(await trigger.text()).toBe('50');
  });

  it('cancels a keyboard-opened size menu without emitting and restores trigger focus', async () => {
    const { pager, size } = await render();
    const trigger = await pager.trigger();
    await trigger.focus();
    await trigger.sendKeys(TestKey.ENTER);
    const current = await pager.sizeOption(20);
    expect(await current.getAttribute('aria-selected')).toBe('true');
    await current.sendKeys(TestKey.ESCAPE);
    expect(await pager.options()).toHaveLength(0);
    expect(await trigger.isFocused()).toBe(true);
    expect(await trigger.text()).toBe('20');
    expect(size).not.toHaveBeenCalled();
  });

  it('refuses malformed or unsupported size changes from the select', async () => {
    const { fixture, size } = await render();
    const select = fixture.debugElement.query(By.directive(HlmSelect));
    for (const invalid of [null, '50', 25]) {
      select.triggerEventHandler('ngModelChange', invalid);
    }
    expect(size).not.toHaveBeenCalled();
    select.triggerEventHandler('ngModelChange', 50);
    expect(size.mock.calls).toEqual([[50]]);
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

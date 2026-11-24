import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ApiError } from '../../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../../shared/testing/layout.testing';
import { AttemptsTableComponent } from './attempts-table.component';
import { render, row } from './attempts-table.test-support';

class AttemptsHarness extends ComponentHarness {
  static hostSelector = 'app-attempts-table';
  readonly next = this.locatorFor('[data-page="next"]');
  readonly previous = this.locatorFor('[data-page="previous"]');
  readonly summary = this.locatorFor('app-data-table-pager [role="status"]');
  readonly trigger = this.locatorFor('hlm-select-trigger button');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly rows = this.locatorForAll('[data-attempt-row]');
  readonly retry = this.locatorFor('app-error-pattern button');
  readonly region = this.locatorForOptional('.table-scroll');
  readonly heading = this.locatorFor('#attempts-title');
  readonly announcement = this.locatorFor('[data-attempts-status]');
  readonly order = this.locatorFor('[data-attempts-order]');
}

const networkFailure: ApiError = {
  kind: 'network',
  status: 0,
  code: '',
  message: '',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

describe('AttemptsTableComponent server pagination', () => {
  provideLayoutTestStubs();
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [AttemptsTableComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  it('renders the supplied server page without local slicing and emits independent page intents', async () => {
    const input = Array.from({ length: 100 }, (_, index) =>
      row({ executionKey: `execution-${index}` }),
    );
    const { fixture } = await render(input);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, AttemptsHarness);
    const next = vi.fn();
    const previous = vi.fn();
    fixture.componentInstance.next.subscribe(next);
    fixture.componentInstance.previous.subscribe(previous);
    fixture.componentRef.setInput('hasMore', true);
    fixture.componentRef.setInput('pageSize', 100);
    expect(await table.rows()).toHaveLength(100);
    expect(await (await table.summary()).text()).toBe('Page 1');
    expect(await (await table.order()).text()).toBe(
      'Server order: execution key ascending, then attempt number ascending.',
    );
    await (await table.previous()).click();
    expect(previous).not.toHaveBeenCalled();
    await (await table.next()).focus();
    await (await table.next()).click();
    expect(next).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('loadingMore', true);
    await (await table.next()).click();
    expect(next).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('attempts', [row({ executionKey: 'last-page' })]);
    fixture.componentRef.setInput('page', 2);
    fixture.componentRef.setInput('hasMore', false);
    fixture.componentRef.setInput('loadingMore', false);
    expect(await table.rows()).toHaveLength(1);
    expect(await (await table.summary()).text()).toBe('Page 2');
    expect(await (await table.next()).isFocused()).toBe(true);
    await (await table.next()).click();
    expect(next).toHaveBeenCalledOnce();
    await (await table.previous()).click();
    expect(previous).toHaveBeenCalledOnce();
  });

  it('offers only 20, 50 and 100 rows and emits the selected size', async () => {
    const { fixture } = await render([row()]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, AttemptsHarness);
    const changed = vi.fn();
    fixture.componentInstance.pageSizeChange.subscribe(changed);
    expect(await (await table.trigger()).text()).toContain('20');
    await (await table.trigger()).click();
    const options = await table.options();
    expect(await Promise.all(options.map((option) => option.text()))).toEqual(['20', '50', '100']);
    await options[1]?.click();
    expect(changed).toHaveBeenCalledExactlyOnceWith(50);
  });

  it('keeps previous navigation and page size reachable on an empty last page', async () => {
    const { fixture } = await render([]);
    fixture.componentRef.setInput('page', 2);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, AttemptsHarness);
    expect(await (await table.summary()).text()).toBe('Page 2');
    expect(await (await table.previous()).getAttribute('aria-disabled')).toBe('false');
    expect(await (await table.next()).getAttribute('aria-disabled')).toBe('true');
    expect(await table.trigger()).toBeTruthy();
  });

  it.each([true, false])(
    'keeps retry focus meaningful with retained rows: %s',
    async (retained) => {
      const { fixture } = await render(retained ? [row()] : null);
      fixture.componentRef.setInput('status', retained ? 'ready' : 'error');
      fixture.componentRef.setInput('error', networkFailure);
      const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, AttemptsHarness);
      const retry = vi.fn();
      fixture.componentInstance.retry.subscribe(retry);
      await (await table.retry()).focus();
      await (await table.retry()).click();
      expect(retry).toHaveBeenCalledOnce();
      fixture.componentRef.setInput('error', null);
      fixture.componentRef.setInput('loadingMore', true);
      if (!retained) fixture.componentRef.setInput('status', 'loading');
      await fixture.whenStable();
      const destination = retained ? await table.region() : await table.heading();
      expect(await destination?.isFocused()).toBe(true);
    },
  );

  it('announces loading outside the busy table and no false completion after failure', async () => {
    const { fixture, root } = await render([row()]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, AttemptsHarness);
    fixture.componentRef.setInput('loadingMore', true);
    expect(await (await table.region())?.getAttribute('aria-busy')).toBe('true');
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(root.querySelector('app-live-status')?.textContent).toContain('Loading attempts page');
    });
    expect(root.querySelector('.table-scroll [data-attempts-status]')).toBeNull();
    fixture.componentRef.setInput('error', networkFailure);
    fixture.componentRef.setInput('loadingMore', false);
    expect(await (await table.announcement()).text()).toBe('');
    expect(await table.rows()).toHaveLength(1);
  });

  it('announces a successfully replaced page', async () => {
    const { fixture } = await render([row()]);
    const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, AttemptsHarness);
    fixture.componentRef.setInput('loadingMore', true);
    await fixture.whenStable();
    fixture.componentRef.setInput('attempts', [row({ executionKey: 'next-page' })]);
    fixture.componentRef.setInput('loadingMore', false);
    expect(await (await table.announcement()).text()).toBe('Attempts page loaded');
    expect(await table.rows()).toHaveLength(1);
  });
});

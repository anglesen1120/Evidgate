import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import type { AuditEntryDto } from '@evidgate/sdk';
import { AuditDataService } from '../../../core/data/audit-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { AuditPage } from './audit.page';
import { AuditTableComponent } from './audit-table.component';
import { AuditStore } from './audit.store';

provideLayoutTestStubs();

function row(index: number, createdAt = '2026-10-25T00:00:00Z'): AuditEntryDto {
  return {
    id: `event-${index}`,
    seq: index,
    actor_type: 'user',
    actor_id: 'member-1',
    action: 'auth.login',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: createdAt,
  };
}

class AuditPagingHarness extends ComponentHarness {
  static hostSelector = 'app-audit-page';
  readonly next = this.locatorFor('[data-page="next"]');
  readonly previous = this.locatorFor('[data-page="previous"]');
  readonly times = this.locatorForAll('[data-row-id] time');
  readonly views = this.locatorForAll('[data-audit-view]');
  readonly pager = this.locatorFor('app-data-table-pager');
  readonly action = this.locatorFor('input[name="action"]');
  readonly apply = this.locatorFor('button[type="submit"]');
  readonly size = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly retry = this.locatorFor('[data-audit-paging-error] button');
  readonly pagingError = this.locatorFor('[data-audit-paging-error]');
  async hasNavigation(): Promise<boolean> {
    return (
      (await this.locatorForOptional(
        'app-data-table-pager [data-page], app-data-table-pager nav[aria-label]',
      )()) !== null
    );
  }
}

class AuditDialogHarness extends ComponentHarness {
  static hostSelector = '[data-audit-dialog]';
  readonly close = this.locatorFor('button[hlmDialogClose]');
  readonly action = this.locatorFor('[hlmDialogDescription] bdi');
  readonly time = this.locatorFor('app-absolute-time');
}

class AuditTableBoundaryHarness extends ComponentHarness {
  static hostSelector = 'app-audit-table';
  readonly action = this.locatorFor('app-data-table-value[title]');
  readonly time = this.locatorFor('app-absolute-time');
  readonly dates = this.locatorForAll('time');
  readonly view = this.locatorFor('[data-audit-view]');
}

class AuditPageSizeOptionHarness extends ComponentHarness {
  static hostSelector = '[role="option"]';
}

function required<T extends Element>(root: HTMLElement, selector: string): T {
  const element = root.querySelector<T>(selector);
  if (!element) throw new Error(`Missing ${selector}`);
  return element;
}

describe('AuditPage boundary browsing and export scope', () => {
  const org = signal<{ id: string } | null>({ id: 'org-1' });
  const list = vi.fn<AuditDataService['list']>();

  beforeEach(() => {
    org.set({ id: 'org-1' });
    list.mockReset();
    TestBed.configureTestingModule({
      imports: [AuditPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuditDataService, useValue: { list } },
        { provide: SessionStore, useValue: { currentOrg: org } },
        { provide: ActivatedRoute, useValue: { snapshot: { data: { pageHeadingLevel: 1 } } } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each([0, 1, 19, 20, 21, 39, 40, 41])(
    'visits every server page exactly once for %s events',
    async (count) => {
      const entries = Array.from({ length: count }, (_, index) =>
        row(index, new Date(Date.UTC(2026, 9, 25, 0, 0, index)).toISOString()),
      );
      list.mockImplementation((_org, query, cursor) => {
        const start = Number(cursor ?? 0);
        const end = start + (query?.limit ?? 20);
        return Promise.resolve({
          items: entries.slice(start, end),
          next_cursor: end < count ? String(end) : null,
        });
      });
      const fixture = TestBed.createComponent(AuditPage);
      await fixture.whenStable();
      const store = fixture.debugElement.injector.get(AuditStore);
      const harness = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        AuditPagingHarness,
      );
      const seen: string[] = [];
      for (let page = 0; page < Math.max(1, Math.ceil(count / 20)); page++) {
        seen.push(
          ...(await Promise.all(
            (await harness.times()).map(
              async (time) => (await time.getAttribute('datetime')) ?? '',
            ),
          )),
        );
        expect(store.rows()).toHaveLength(entries.slice(page * 20, page * 20 + 20).length);
        expect(store.page()).toBe(page + 1);
        expect(await harness.pager()).toBeDefined();
        if (store.hasNext()) await (await harness.next()).click();
      }
      expect(seen).toEqual(entries.map((event) => event.created_at));
      expect(await harness.hasNavigation()).toBe(count > 20);
      expect(store.page()).toBe(Math.max(1, Math.ceil(count / 20)));
      expect(list).toHaveBeenCalledTimes(Math.max(1, Math.ceil(count / 20)));
    },
  );

  it('retains the current page and size when opening and closing View', async () => {
    const entries = Array.from({ length: 45 }, (_, index) => row(index));
    list.mockImplementation((_org, query, cursor) => {
      const start = Number(cursor ?? 0);
      const end = start + (query?.limit ?? 20);
      return Promise.resolve({
        items: entries.slice(start, end),
        next_cursor: end < entries.length ? String(end) : null,
      });
    });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AuditPagingHarness);
    await (await harness.next()).click();
    const retained = store.rows();
    const views = await harness.views();
    if (!views[0]) throw new Error('Expected View action');
    await views[0].click();
    const documentLoader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const dialog = await documentLoader.getHarness(AuditDialogHarness);
    expect(store.page()).toBe(2);
    await (await dialog.close()).click();
    expect(await documentLoader.hasHarness(AuditDialogHarness)).toBe(false);
    expect(store.rows()).toBe(retained);
    expect(store.page()).toBe(2);
    expect(store.pageSize()).toBe(20);
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('resets the server page when filters or the shared page-size control change', async () => {
    const entries = Array.from({ length: 65 }, (_, index) => row(index));
    list.mockImplementation((_org, query, cursor) => {
      const start = Number(cursor ?? 0);
      const end = start + (query?.limit ?? 20);
      return Promise.resolve({
        items: entries.slice(start, end),
        next_cursor: end < entries.length ? String(end) : null,
      });
    });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AuditPagingHarness);
    await (await harness.next()).click();
    const action = await harness.action();
    await action.setInputValue('auth.login');
    await action.dispatchEvent('input');
    await (await harness.apply()).click();
    expect(store.page()).toBe(1);
    expect(store.filters().action).toBe('auth.login');
    expect(list).toHaveBeenLastCalledWith('org-1', { limit: 20, action: 'auth.login' });
    await (await harness.next()).click();
    await (await harness.size()).click();
    const options = await TestbedHarnessEnvironment.documentRootLoader(fixture).getAllHarnesses(
      AuditPageSizeOptionHarness,
    );
    for (const option of options) {
      const host = await option.host();
      if ((await host.text()).trim() === '50') await host.click();
    }
    expect(store.pageSize()).toBe(50);
    expect(store.page()).toBe(1);
    expect(store.filters().action).toBe('auth.login');
    expect(list).toHaveBeenLastCalledWith('org-1', { limit: 50, action: 'auth.login' });
  });

  it('preserves successful rows and disables manual retry for a later-page Retry-After', async () => {
    list.mockResolvedValueOnce({ items: [row(1)], next_cursor: 'next' });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AuditPagingHarness);
    list.mockRejectedValueOnce(
      new ApiRequestError({
        kind: 'rate_limited',
        status: 429,
        code: 'wait',
        message: 'Wait before retrying',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: 60,
      }),
    );
    await (await harness.next()).click();
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual([row(1)]);
    expect(await (await harness.pagingError()).text()).toContain('Wait before retrying');
    expect(await (await harness.retry()).getProperty('disabled')).toBe(true);
    const next = await harness.next();
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await next.getProperty('disabled')).toBe(false);
    await next.focus();
    await next.click();
    await next.sendKeys(TestKey.ENTER);
    expect(await next.isFocused()).toBe(true);
    await (await harness.retry()).click();
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual([row(1)]);
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('keeps Previous available when the next cursor returns an empty last page', async () => {
    list
      .mockResolvedValueOnce({ items: [row(1)], next_cursor: 'empty' })
      .mockResolvedValueOnce({ items: [], next_cursor: null })
      .mockResolvedValueOnce({ items: [row(1)], next_cursor: 'empty' });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AuditPagingHarness);
    await (await harness.next()).click();
    expect(store.page()).toBe(2);
    expect(store.rows()).toEqual([]);
    expect(await (await harness.previous()).getProperty('disabled')).toBe(false);
    await (await harness.previous()).click();
    expect(store.page()).toBe(1);
    expect(store.rows()).toEqual([row(1)]);
  });

  it('blocks edited export scope until Apply and enables again after reverting the draft', async () => {
    list.mockResolvedValue({ items: [row(1)], next_cursor: null });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const action = required<HTMLInputElement>(root, '[name="action"]');
    const exportButton = required<HTMLButtonElement>(root, '[data-audit-export]');
    expect(exportButton.disabled).toBe(false);
    action.value = 'run.create';
    action.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(exportButton.disabled).toBe(true);
    expect(root.querySelector('#audit-export-draft-hint')).not.toBeNull();
    exportButton.click();
    expect(list).toHaveBeenCalledTimes(1);
    action.value = '';
    action.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(exportButton.disabled).toBe(false);
    expect(root.querySelector('#audit-export-draft-hint')).toBeNull();
  });

  it('applies a typed action before resetting page size through the shared control', async () => {
    list.mockResolvedValue({ items: [row(1)], next_cursor: 'next' });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AuditPagingHarness);
    await (await harness.next()).click();
    await (await harness.action()).sendKeys('auth.login');
    await (await harness.apply()).click();
    expect(store.filters().action).toBe('auth.login');
    expect(store.page()).toBe(1);
    expect(list).toHaveBeenLastCalledWith('org-1', { limit: 20, action: 'auth.login' });
    await (await harness.size()).click();
    const options = await TestbedHarnessEnvironment.documentRootLoader(fixture).getAllHarnesses(
      AuditPageSizeOptionHarness,
    );
    const choices = await Promise.all(
      options.map(async (option) => {
        const host = await option.host();
        return { host, text: await host.text() };
      }),
    );
    const selected = choices.find((option) => option.text.trim() === '50');
    if (!selected) throw new Error('Expected the supported 50-row option');
    await selected.host.click();
    expect(store.pageSize()).toBe(50);
    expect(store.page()).toBe(1);
    expect(store.filters().action).toBe('auth.login');
    expect(list).toHaveBeenLastCalledWith('org-1', { limit: 50, action: 'auth.login' });
  });

  it('hides controls on a terminal first page and refuses activation of disabled Next on a terminal later page', async () => {
    list
      .mockResolvedValueOnce({ items: [row(1)], next_cursor: 'c1' })
      .mockResolvedValueOnce({ items: [row(2)], next_cursor: null });
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, AuditPagingHarness);
    await (await harness.next()).click();
    expect(fixture.debugElement.injector.get(AuditStore).page()).toBe(2);
    const next = await harness.next();
    const previous = await harness.previous();
    expect(await next.getProperty('disabled')).toBe(false);
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await previous.getAttribute('aria-disabled')).not.toBe('true');
    await next.focus();
    expect(await next.isFocused()).toBe(true);
    await next.click();
    await next.sendKeys(TestKey.ENTER);
    expect(await next.isFocused()).toBe(true);
    expect(list).toHaveBeenCalledTimes(2);
    expect(fixture.debugElement.injector.get(AuditStore).rows()).toEqual([row(2)]);
  });
});

describe('AuditTableComponent invalid timestamp boundary', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(['not-a-date', '2026-99-99T25:99:99Z'])(
    'keeps the activity readable for malformed timestamp %s',
    async (timestamp) => {
      TestBed.configureTestingModule({
        imports: [AuditTableComponent],
        providers: [provideZonelessChangeDetection()],
      });
      const fixture = TestBed.createComponent(AuditTableComponent);
      fixture.componentRef.setInput('rows', [row(1, timestamp)]);
      await fixture.whenStable();
      const harness = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        AuditTableBoundaryHarness,
      );
      expect(await (await harness.action()).text()).toBe('Signed in');
      expect(await (await harness.action()).getAttribute('title')).toBe('auth.login');
      expect(await (await harness.time()).text()).toBe('Time unavailable');
      expect(await harness.dates()).toHaveLength(0);
      await (await harness.view()).click();
      const dialog =
        await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(AuditDialogHarness);
      expect(await (await dialog.action()).text()).toBe('auth.login');
      expect(await (await dialog.time()).text()).toBe('Time unavailable');
      await (await dialog.close()).click();
    },
  );
});

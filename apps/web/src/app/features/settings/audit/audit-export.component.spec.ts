import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';
import type { Mock, MockInstance } from 'vitest';
import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { AuditExportComponent } from './audit-export.component';
import { AUDIT_EXPORT_MAX_PAGES, AuditStore } from './audit.store';

function entry(seq: number): AuditEntryDto {
  return {
    id: `row-${seq}`,
    seq,
    actor_type: 'user',
    actor_id: 'member1',
    action: 'project.updated',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: '2026-10-25T00:00:00Z',
  };
}
const refused: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'audit_unavailable',
  message: 'Audit read refused. <b>Keep this text.</b>',
  requestId: 'request1',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};
/**
 * `Promise.withResolvers` is ES2024 and this project compiles against ES2022, so this spec carries
 * its own equivalent — the hand-rolled resolver that
 * `features/onboarding/steps/create-project.page.spec.ts` builds inline, lifted into one helper
 * because two cases here need it.
 */
function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}
class FakeAuditDataService {
  rows: AuditEntryDto[] = [];
  readonly list = vi.fn<
    (org: string, query: AuditListQuery, cursor?: string) => Promise<AuditListResponse>
  >((_org, query, cursor) => {
    const start = Number(cursor ?? 0);
    const end = Math.min(start + (query.limit ?? 50), this.rows.length);
    return Promise.resolve({
      items: this.rows.slice(start, end),
      next_cursor: end < this.rows.length ? String(end) : null,
    });
  });
}

describe('AuditExportComponent', () => {
  let data: FakeAuditDataService;
  let click: MockInstance<() => void>;
  let createUrl: Mock<(value: Blob | MediaSource) => string>;
  let revokeUrl: Mock<(url: string) => void>;
  beforeEach(() => {
    data = new FakeAuditDataService();
    createUrl = vi.fn(() => 'blob:audit-download');
    revokeUrl = vi.fn();
    vi.stubGlobal(
      'URL',
      class extends URL {
        static override createObjectURL = createUrl;
        static override revokeObjectURL = revokeUrl;
      },
    );
    click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    configure();
  });
  /**
   * `LOCALE_ID` is injected by the component, so the locale cases need their own TestBed. The
   * default run stays at Angular's `en-US`, where `formatNumber` needs no locale data.
   */
  function configure(localeId?: string): void {
    TestBed.configureTestingModule({
      imports: [AuditExportComponent],
      providers: [
        provideZonelessChangeDetection(),
        AuditStore,
        { provide: AuditDataService, useValue: data },
        ...(localeId === undefined ? [] : [{ provide: LOCALE_ID, useValue: localeId }]),
      ],
    });
  }
  /**
   * The revoke is deferred one task so the download can start (P3 review), so the assertion has to
   * wait for a task boundary, not for `whenStable()`.
   */
  async function flushDeferredRevoke(): Promise<void> {
    // `Promise.withResolvers` is ES2024 and this project compiles against ES2022 (see `deferred`
    // above), so the executor form is the only one available here.
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  afterEach(async () => {
    // The deferred revoke is a real timer: drain it before the URL stubs are restored so no
    // callback runs against the next test's stubs.
    await flushDeferredRevoke();
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });
  async function render() {
    const fixture = TestBed.createComponent(AuditExportComponent);
    fixture.componentRef.setInput('orgId', 'org1');
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('keeps the export action labelled and focusable, and disables it without an organization', async () => {
    const { fixture, root } = await render();
    const action = root.querySelector<HTMLButtonElement>('[data-audit-export]');
    if (!action) throw new Error('Expected export action');
    expect(action.getAttribute('data-slot')).toBe('button');
    expect(action.textContent?.trim()).toBe('Export CSV');
    action.focus();
    expect(document.activeElement).toBe(action);
    fixture.componentRef.setInput('orgId', '');
    await fixture.whenStable();
    expect(action.disabled).toBe(true);
    action.click();
    expect(data.list).not.toHaveBeenCalled();
    expect(click).not.toHaveBeenCalled();
  });
  it('keeps the CSV utility content-sized and start-aligned without losing its scope', async () => {
    const { root } = await render();
    const action = root.querySelector<HTMLButtonElement>('[data-audit-export]');
    if (!action) throw new Error('Expected export action');
    expect(action.classList.contains('w-fit')).toBe(true);
    expect(action.classList.contains('max-w-full')).toBe(true);
    expect(action.classList.contains('justify-self-start')).toBe(true);
    expect(action.classList.contains('min-h-[var(--eg-control-touch)]')).toBe(true);
    expect(action.getAttribute('aria-describedby')).toBe('audit-export-scope-title');
    expect(data.list).not.toHaveBeenCalled();
  });
  it('fetches exactly 50 pages of a 15,000 row fixture, downloads once and persistently announces the exact cap', async () => {
    data.rows = Array.from({ length: 15_000 }, (_, index) => entry(index));
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(50);
    expect(
      data.list.mock.calls.every(([org, query]) => org === 'org1' && query.limit === 200),
    ).toBe(true);
    expect(root.querySelector('[data-audit-export-capped]')?.textContent?.trim()).toBe(
      'First 10,000 rows exported — narrow the filter for a complete export.',
    );
    expect(root.querySelector('[data-audit-export-capped]')?.getAttribute('role')).toBe('status');
    expect(click).toHaveBeenCalledTimes(1);
    expect(createUrl).toHaveBeenCalledTimes(1);
    const anchor = click.mock.instances[0] as HTMLAnchorElement;
    expect(anchor.download).toMatch(/^evidgate-audit-org1-\d{4}-\d{2}-\d{2}\.csv$/);
    expect(anchor.isConnected).toBe(false);
    expect((createUrl.mock.calls[0] as unknown as [Blob])[0].type).toBe('text/csv;charset=utf-8');
    // P3: the URL is revoked on a later task, after the anchor has been clicked and detached.
    await flushDeferredRevoke();
    expect(revokeUrl).toHaveBeenCalledTimes(1);
    expect(revokeUrl).toHaveBeenCalledWith('blob:audit-download');
    fixture.detectChanges();
    expect(root.querySelector('[data-audit-export-capped]')).not.toBeNull();
  });
  it('formats the capped row count with the active locale: vi groups it as 10.000, en as 10,000', async () => {
    registerLocaleData(viLocale);
    configure('vi');
    data.rows = Array.from({ length: 15_000 }, (_, index) => entry(index));
    const { fixture, root } = await render();
    expect(TestBed.inject(LOCALE_ID)).toBe('vi');
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    // The en run of the first case proves the `10,000` branch; this one proves the number follows
    // `LOCALE_ID` instead of a hard-coded en-US grouping (the defect A5 reported).
    expect(TestBed.inject(AuditStore).exportState()).toEqual({
      phase: 'done',
      rows: 10_000,
      capped: true,
    });
    // A unit test runs the source locale, so the surrounding copy is English; only the number
    // follows the injected locale.
    expect(root.querySelector('[data-audit-export-capped]')?.textContent?.trim()).toBe(
      'First 10.000 rows exported — narrow the filter for a complete export.',
    );
    expect(click).toHaveBeenCalledTimes(1);
  });
  it('prints the rows actually exported when the safety page bound caps a short-page export', async () => {
    // F-4: `capped` is also true when the loop stops at its page bound with a cursor still named.
    // One-row pages never reach 10,000 rows, so the banner must print the collected count, not the
    // cap — the constant banner printed 10,000 whatever the store had actually exported.
    data.list.mockImplementation((_org, _query, cursor) => {
      const index = Number(cursor ?? 0);
      return Promise.resolve({ items: [entry(index)], next_cursor: String(index + 1) });
    });
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(AUDIT_EXPORT_MAX_PAGES);
    expect(TestBed.inject(AuditStore).exportState()).toEqual({
      phase: 'done',
      rows: AUDIT_EXPORT_MAX_PAGES,
      capped: true,
    });
    expect(root.querySelector('[data-audit-export-capped]')?.textContent?.trim()).toBe(
      `First ${AUDIT_EXPORT_MAX_PAGES} rows exported — narrow the filter for a complete export.`,
    );
    // A bounded export still hands the rows it did collect to the browser.
    expect(click).toHaveBeenCalledTimes(1);
  });
  it('announces a complete small result without a cap banner', async () => {
    data.rows = [entry(1), entry(2)];
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('button')!.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-audit-export-done]')?.textContent).toBe('Exported 2 rows.');
    expect(root.querySelector('[data-audit-export-capped]')).toBeNull();
    expect(click).toHaveBeenCalledTimes(1);
    await flushDeferredRevoke();
    expect(revokeUrl).toHaveBeenCalledWith('blob:audit-download');
  });
  it('shows a refusal verbatim as text and retries explicitly', async () => {
    data.list.mockRejectedValueOnce(new ApiRequestError(refused));
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    const error = root.querySelector('[data-audit-export-error]')!;
    expect(error.textContent).toContain(refused.message);
    expect(error.textContent).toContain(refused.code);
    expect(error.querySelector('b')).toBeNull();
    expect(click).not.toHaveBeenCalled();
    error.querySelector<HTMLButtonElement>('button')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-audit-export-error]')).toBeNull();
  });
  it('disables duplicate export and exposes polite progress while a page is pending', async () => {
    const pending = deferred<AuditListResponse>();
    data.list.mockReturnValueOnce(pending.promise);
    const { fixture, root } = await render();
    const button = root.querySelector<HTMLButtonElement>('[data-audit-export]')!;
    button.click();
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    expect(root.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(root.querySelector('[aria-live="polite"]')?.textContent).toContain('Exporting… 0 rows');
    button.click();
    expect(data.list).toHaveBeenCalledTimes(1);
    pending.resolve({ items: [entry(1)], next_cursor: null });
    await fixture.whenStable();
    // Zoneless: a fake promise settling is not a pending task, so `whenStable()` can resolve with
    // the view still rendered from `running() === true` — the store has settled, the DOM has not.
    fixture.detectChanges();
    expect(TestBed.inject(AuditStore).exportState().phase).toBe('done');
    expect(button.disabled).toBe(false);
    expect(root.querySelector('[aria-busy="false"]')).not.toBeNull();
  });
  it('does not download an old organization result after switching scope', async () => {
    const pending = deferred<AuditListResponse>();
    data.list.mockReturnValueOnce(pending.promise);
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('button')!.click();
    fixture.componentRef.setInput('orgId', 'org2');
    pending.resolve({ items: [entry(1)], next_cursor: null });
    await fixture.whenStable();
    expect(click).not.toHaveBeenCalled();
    expect(createUrl).not.toHaveBeenCalled();
    // A skipped download leaves no blob URL behind, so the deferred revoke timer never fires.
    await flushDeferredRevoke();
    expect(revokeUrl).not.toHaveBeenCalled();
  });
  it('keeps the blob URL alive until download() settles and revokes it on a later task', async () => {
    data.rows = [entry(1)];
    const order: string[] = [];
    click.mockImplementation(() => {
      order.push('click');
    });
    revokeUrl.mockImplementation(() => {
      order.push('revoke');
    });
    const { fixture } = await render();
    const component = fixture.componentInstance as unknown as { download(): Promise<void> };
    await component.download();
    // The defect was a revoke inside `finally`, in the same task as `click()`: this order would
    // already read ['click', 'revoke']. Only microtasks ran between the click and this assertion.
    expect(order).toEqual(['click']);
    await flushDeferredRevoke();
    expect(order).toEqual(['click', 'revoke']);
  });
  it('revokes the blob URL even when the component is destroyed before the deferred task runs', async () => {
    data.rows = [entry(1)];
    const { fixture } = await render();
    const component = fixture.componentInstance as unknown as { download(): Promise<void> };
    await component.download();
    expect(revokeUrl).not.toHaveBeenCalled();
    fixture.destroy();
    await flushDeferredRevoke();
    // Destroying the view does not cancel the revoke: an unreleased object URL keeps the blob.
    expect(revokeUrl).toHaveBeenCalledTimes(1);
    expect(revokeUrl).toHaveBeenCalledWith('blob:audit-download');
  });
  it('still detaches the anchor and revokes the URL when the click throws', async () => {
    data.rows = [entry(1)];
    click.mockImplementation(() => {
      throw new Error('navigation blocked');
    });
    const { fixture } = await render();
    const component = fixture.componentInstance as unknown as { download(): Promise<void> };
    await expect(component.download()).rejects.toThrow('navigation blocked');
    // `finally` runs on the throw: no stray anchor, and the blob URL is still released.
    expect(document.body.querySelector('a[download]')).toBeNull();
    await flushDeferredRevoke();
    expect(revokeUrl).toHaveBeenCalledTimes(1);
    expect(revokeUrl).toHaveBeenCalledWith('blob:audit-download');
  });
  it('does not claim truncation when exactly 10,000 rows exhaust the cursor', async () => {
    data.rows = Array.from({ length: 10_000 }, (_, index) => entry(index));
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(50);
    expect(root.querySelector('[data-audit-export-capped]')).toBeNull();
    expect(root.querySelector('[data-audit-export-done]')?.textContent).toBe(
      'Exported 10,000 rows.',
    );
  });
  it('uses the currently applied filters for every export page', async () => {
    data.rows = Array.from({ length: 201 }, (_, index) => entry(index));
    const store = TestBed.inject(AuditStore);
    await store.load('org1', {
      actorType: 'system',
      action: 'run.created',
      objectType: 'run',
      from: '2026-10-20T00:00:00.000Z',
      to: '2026-10-21T00:00:00.000Z',
    });
    data.list.mockClear();
    const { fixture, root } = await render();
    const scope = root.querySelector('section[aria-labelledby="audit-export-scope-title"]');
    expect(scope?.textContent).toContain('org1');
    expect(scope?.textContent).toContain('System');
    expect(scope?.textContent).toContain('run.created');
    expect(scope?.textContent).toContain('run');
    const times = scope?.querySelectorAll('time') ?? [];
    expect([...times].map((time) => time.getAttribute('datetime'))).toEqual([
      '2026-10-20T00:00:00.000Z',
      '2026-10-21T00:00:00.000Z',
    ]);
    for (const time of times) {
      expect(time.textContent).toContain('UTC');
      expect(time.textContent).not.toContain(time.getAttribute('datetime'));
    }
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(2);
    for (const [, query] of data.list.mock.calls) {
      expect(query).toEqual({
        actor_type: 'system',
        action: 'run.created',
        object_type: 'run',
        from: '2026-10-20T00:00:00.000Z',
        to: '2026-10-21T00:00:00.000Z',
        limit: 200,
      });
    }
  });
  it.each(['en-US', 'vi'])(
    'formats applied export bounds in %s without changing UTC filter precision',
    async (locale) => {
      TestBed.resetTestingModule();
      registerLocaleData(viLocale);
      configure(locale);
      const store = TestBed.inject(AuditStore);
      const from = '2026-10-20T07:30:00.125+07:00';
      const to = '2026-10-21T07:30:00.125+07:00';
      await store.load('org1', { actorType: '', action: '', objectType: '', from, to });
      const { root } = await render();
      const times = root.querySelectorAll('time');
      expect([...times].map((time) => time.getAttribute('datetime'))).toEqual([from, to]);
      expect(times).toHaveLength(2);
      expect(times[0]?.textContent).toContain(locale === 'vi' ? '20/10/2026' : '10/20/2026');
      expect(times[0]?.textContent).toContain('UTC');
      expect(root.textContent).toContain('From (inclusive');
      expect(root.textContent).toContain('To (exclusive');
    },
  );
  it('blocks export with an associated explanation until draft filters are applied', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('hasDraftChanges', true);
    await fixture.whenStable();
    const action = root.querySelector<HTMLButtonElement>('[data-audit-export]');
    expect(action?.disabled).toBe(true);
    expect(action?.getAttribute('aria-describedby')).toBe('audit-export-draft-hint');
    expect(root.querySelector('#audit-export-draft-hint')?.textContent).toContain('Apply or clear');
    action?.click();
    expect(data.list).not.toHaveBeenCalled();
    fixture.componentRef.setInput('hasDraftChanges', false);
    await fixture.whenStable();
    expect(action?.disabled).toBe(false);
    expect(root.querySelector('#audit-export-draft-hint')).toBeNull();
    expect(root.querySelector('dl')?.textContent).toContain('No time limit');
  });

  it.each([
    ['en', '1,200'],
    ['vi', '1.200'],
  ])('formats progress and completion through the %s locale', async (locale, count) => {
    registerLocaleData(viLocale);
    configure(locale);
    data.rows = Array.from({ length: 1_200 }, (_, index) => entry(index));
    const normal = data.list.getMockImplementation();
    if (!normal) throw new Error('Expected paged data implementation');
    const pending = deferred<AuditListResponse>();
    const last = data.rows.length;
    data.list.mockImplementation((org, query, cursor) => {
      if (cursor === String(last)) return pending.promise;
      return normal(org, query, cursor).then((page) =>
        page.next_cursor === null ? { ...page, next_cursor: String(last) } : page,
      );
    });
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')?.click();
    await vi.waitFor(() => expect(data.list).toHaveBeenCalledTimes(7));
    fixture.detectChanges();
    expect(root.querySelector('[aria-live="polite"]')?.textContent).toContain(
      `Exporting… ${count} rows`,
    );
    pending.resolve({ items: [], next_cursor: null });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root.querySelector('[data-audit-export-done]')?.textContent).toBe(
      `Exported ${count} rows.`,
    );
  });
});

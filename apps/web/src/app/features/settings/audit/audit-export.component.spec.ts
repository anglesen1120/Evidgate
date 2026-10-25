import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';
import type { Mock, MockInstance } from 'vitest';
import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { AuditExportComponent } from './audit-export.component';
import { AuditStore } from './audit.store';

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

describe('AuditExportComponent (AC7)', () => {
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
    TestBed.configureTestingModule({
      imports: [AuditExportComponent],
      providers: [
        provideZonelessChangeDetection(),
        AuditStore,
        { provide: AuditDataService, useValue: data },
      ],
    });
  });
  afterEach(() => {
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
  it('fetches exactly 50 pages of a 15,000 row fixture, downloads once and persistently announces the exact cap', async () => {
    data.rows = Array.from({ length: 15_000 }, (_, index) => entry(index));
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(50);
    expect(
      data.list.mock.calls.every(([org, query]) => org === 'org1' && query.limit === 200),
    ).toBe(true);
    expect(root.querySelector('[data-audit-export-capped]')?.textContent).toBe(
      'First 10,000 rows exported — narrow the filter for a complete export.',
    );
    expect(root.querySelector('[data-audit-export-capped]')?.getAttribute('role')).toBe('status');
    expect(click).toHaveBeenCalledTimes(1);
    expect(createUrl).toHaveBeenCalledTimes(1);
    expect(revokeUrl).toHaveBeenCalledWith('blob:audit-download');
    const anchor = click.mock.instances[0] as HTMLAnchorElement;
    expect(anchor.download).toMatch(/^evidgate-audit-org1-\d{4}-\d{2}-\d{2}\.csv$/);
    expect(anchor.isConnected).toBe(false);
    expect((createUrl.mock.calls[0] as unknown as [Blob])[0].type).toBe('text/csv;charset=utf-8');
    fixture.detectChanges();
    expect(root.querySelector('[data-audit-export-capped]')).not.toBeNull();
  });
  it('announces a complete small result without a cap banner', async () => {
    data.rows = [entry(1), entry(2)];
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('button')!.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-audit-export-done]')?.textContent).toBe('Exported 2 rows.');
    expect(root.querySelector('[data-audit-export-capped]')).toBeNull();
    expect(click).toHaveBeenCalledTimes(1);
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
  });
  it('does not claim truncation when exactly 10,000 rows exhaust the cursor', async () => {
    data.rows = Array.from({ length: 10_000 }, (_, index) => entry(index));
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-audit-export]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(50);
    expect(root.querySelector('[data-audit-export-capped]')).toBeNull();
    expect(root.querySelector('[data-audit-export-done]')?.textContent).toBe(
      'Exported 10000 rows.',
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
});

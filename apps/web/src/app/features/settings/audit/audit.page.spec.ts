import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';
import { AuditDataService, type AuditListQuery } from '../../../core/data/audit-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { AuditPage } from './audit.page';
import { AUDIT_MAX_PAGES, AuditStore, EMPTY_AUDIT_FILTERS } from './audit.store';

provideLayoutTestStubs();

function entry(id: string, changes: Partial<AuditEntryDto> = {}): AuditEntryDto {
  return {
    id,
    seq: 1,
    actor_type: 'user',
    actor_id: 'member1',
    action: 'project.updated',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: '2026-10-25T00:00:00Z',
    ...changes,
  };
}
const refused: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'audit_unavailable',
  message: 'Server refuses this audit request. <b>Keep verbatim.</b>',
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
  readonly list = vi.fn<
    (org: string, query: AuditListQuery, cursor?: string) => Promise<AuditListResponse>
  >(() =>
    Promise.resolve({
      items: [
        entry('ordinary'),
        entry('operator', {
          actor_type: 'platform_operator',
          metadata: { support_grant_id: 'grant-42' },
        }),
      ],
      next_cursor: null,
    }),
  );
}

describe('AuditPage', () => {
  let data: FakeAuditDataService;
  const org = signal<{ id: string } | null>({ id: 'org1' });
  let routeData: Record<string, unknown>;
  const providers = () => [
    provideZonelessChangeDetection(),
    { provide: AuditDataService, useValue: data },
    { provide: SessionStore, useValue: { currentOrg: org } },
    { provide: ActivatedRoute, useFactory: () => ({ snapshot: { data: routeData } }) },
  ];
  beforeEach(() => {
    routeData = {};
    data = new FakeAuditDataService();
    org.set({ id: 'org1' });
    TestBed.configureTestingModule({ imports: [AuditPage], providers: providers() });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.useRealTimers();
  });
  async function render() {
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    return { fixture, store, root: fixture.nativeElement as HTMLElement };
  }
  function input(root: HTMLElement, name: string, value: string): void {
    const field = root.querySelector<HTMLInputElement>(`input[name="${name}"]`);
    if (!field) throw new Error(`Expected input ${name}`);
    field.value = value;
    field.dispatchEvent(new Event('input', { bubbles: true }));
  }
  function submit(root: HTMLElement): void {
    const form = root.querySelector('form');
    if (!form) throw new Error('Expected audit filters form');
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  }
  async function chooseActor(
    fixture: ComponentFixture<AuditPage>,
    root: HTMLElement,
    label: string,
  ): Promise<void> {
    root.querySelector<HTMLButtonElement>('#audit-actor')?.click();
    await fixture.whenStable();
    const option = [...document.querySelectorAll<HTMLElement>('[role="option"]')].find(
      (item) => item.textContent?.trim() === label,
    );
    if (!option) throw new Error(`Expected actor option ${label}`);
    option.click();
    await fixture.whenStable();
  }
  it('loads the current organization into a real scoped store and flags support activity', async () => {
    const { root } = await render();
    expect(data.list).toHaveBeenCalledWith('org1', { limit: 50 });
    expect(root.getAttribute('data-audit-phase')).toBe('ready');
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(2);
    expect(root.querySelector('[data-audit-support="true"]')?.textContent).toContain(
      'Support access',
    );
  });
  it('submits a labelled form with bounded inputs and maps local date inputs to UTC filters', async () => {
    const { fixture, root, store } = await render();
    const load = vi.spyOn(store, 'load');
    await chooseActor(fixture, root, 'CI producer');
    input(root, 'action', 'run.created');
    input(root, 'objectType', 'run');
    input(root, 'from', '2026-10-20T10:30');
    input(root, 'to', '2026-10-21T10:30');
    submit(root);
    await fixture.whenStable();
    expect(load).toHaveBeenLastCalledWith('org1', {
      actorType: 'ci_producer',
      action: 'run.created',
      objectType: 'run',
      from: new Date('2026-10-20T10:30').toISOString(),
      to: new Date('2026-10-21T10:30').toISOString(),
    });
    expect(root.querySelector('[name="action"]')?.getAttribute('maxlength')).toBe('128');
    expect(root.querySelector('[name="objectType"]')?.getAttribute('maxlength')).toBe('64');
    expect(root.querySelectorAll('label')).toHaveLength(5);
    expect(root.querySelector('button[type="submit"]')).not.toBeNull();
    expect(root.querySelector('#audit-actor')?.textContent).toContain('CI producer');
    expect(root.querySelector('label[for="audit-actor"]')).not.toBeNull();
  });
  it('blocks reversed dates inline without calling load and accepts equal boundaries', async () => {
    const { fixture, root, store } = await render();
    const load = vi.spyOn(store, 'load');
    for (const name of ['from', 'to']) {
      expect(root.querySelector(`[name="${name}"]`)?.getAttribute('aria-describedby')).toBe(
        'audit-date-hint',
      );
    }
    expect(root.querySelector('#audit-date-hint')?.textContent).toMatch(/\S/);
    input(root, 'from', '2026-10-22T10:30');
    input(root, 'to', '2026-10-21T10:30');
    submit(root);
    await fixture.whenStable();
    expect(load).not.toHaveBeenCalled();
    expect(root.querySelector('#audit-range-error')?.getAttribute('role')).toBe('alert');
    expect(root.querySelector('[name="from"]')?.getAttribute('aria-invalid')).toBe('true');
    for (const name of ['from', 'to']) {
      expect(root.querySelector(`[name="${name}"]`)?.getAttribute('aria-describedby')).toBe(
        'audit-date-hint audit-range-error',
      );
    }
    expect(root.querySelector('#audit-range-error')?.textContent).toMatch(/\S/);
    input(root, 'to', '2026-10-22T10:30');
    submit(root);
    await fixture.whenStable();
    expect(load).toHaveBeenCalledTimes(1);
    expect(root.querySelector('#audit-range-error')).toBeNull();
    for (const name of ['from', 'to']) {
      expect(root.querySelector(`[name="${name}"]`)?.getAttribute('aria-describedby')).toBe(
        'audit-date-hint',
      );
    }
  });
  it('clears every filter and reloads the first page explicitly', async () => {
    const { fixture, root, store } = await render();
    input(root, 'action', 'run.created');
    submit(root);
    await fixture.whenStable();
    const load = vi.spyOn(store, 'load');
    root.querySelector<HTMLButtonElement>('[data-audit-clear]')!.click();
    await fixture.whenStable();
    expect(load).toHaveBeenLastCalledWith('org1', EMPTY_AUDIT_FILTERS);
    expect((root.querySelector('[name="action"]') as HTMLInputElement).value).toBe('');
  });
  it('reloads on organization switch, resets filters and excludes old rows', async () => {
    const { fixture, root, store } = await render();
    input(root, 'action', 'run.created');
    submit(root);
    await fixture.whenStable();
    data.list.mockResolvedValueOnce({
      items: [entry('new-org', { actor_id: 'new-member' })],
      next_cursor: null,
    });
    const load = vi.spyOn(store, 'load');
    org.set({ id: 'org2' });
    await fixture.whenStable();
    expect(load).toHaveBeenLastCalledWith('org2', EMPTY_AUDIT_FILTERS);
    expect(root.textContent).toContain('new-member');
    expect(root.textContent).not.toContain('member1');
    expect((root.querySelector('[name="action"]') as HTMLInputElement).value).toBe('');
  });
  it('opens actor choices from the keyboard and dismisses without applying a filter', async () => {
    const { fixture, root, store } = await render();
    const load = vi.spyOn(store, 'load');
    const trigger = root.querySelector<HTMLButtonElement>('#audit-actor');
    if (!trigger) throw new Error('Expected actor picker');
    trigger.focus();
    trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', keyCode: 40, bubbles: true }),
    );
    await fixture.whenStable();
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    document.activeElement?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await fixture.whenStable();
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(trigger);
    expect(load).not.toHaveBeenCalled();
  });
  it('shows shared loading state while the first page is pending', async () => {
    const pending = deferred<AuditListResponse>();
    data.list.mockReturnValueOnce(pending.promise);
    const fixture = TestBed.createComponent(AuditPage);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    expect(root.querySelector('[data-audit-row]')).toBeNull();
    pending.resolve({ items: [], next_cursor: null });
    await fixture.whenStable();
  });
  it('renders server error message and code verbatim and retries without an export', async () => {
    data.list.mockRejectedValueOnce(new ApiRequestError(refused));
    const { fixture, root } = await render();
    expect(root.textContent).toContain(refused.message);
    expect(root.textContent).toContain(refused.code);
    expect(root.querySelector('b')).toBeNull();
    root.querySelector<HTMLButtonElement>('app-error-pattern button')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenCalledTimes(2);
    expect(data.list.mock.calls.every(([, query]) => query.limit === 50)).toBe(true);
    expect(root.querySelector('app-error-pattern')).toBeNull();
  });
  it('uses the shared 429 pattern and waits for Retry-After without automatic retry', async () => {
    data.list.mockRejectedValueOnce(
      new ApiRequestError({
        ...refused,
        kind: 'rate_limited',
        status: 429,
        code: 'audit_rate_limited',
        retryAfterSeconds: 60,
      }),
    );
    const { root } = await render();
    expect(root.querySelector('app-retry-after')).not.toBeNull();
    expect(root.querySelector<HTMLButtonElement>('app-retry-after button')?.disabled).toBe(true);
    expect(root.textContent).toContain(refused.message);
    expect(root.textContent).toContain('audit_rate_limited');
    expect(data.list).toHaveBeenCalledTimes(1);
  });
  it('distinguishes empty from filtered no-match and offers a clear action', async () => {
    data.list.mockResolvedValue({ items: [], next_cursor: null });
    const { fixture, root } = await render();
    expect(root.textContent).toContain('No activity recorded');
    input(root, 'action', 'missing.action');
    submit(root);
    await fixture.whenStable();
    expect(root.textContent).toContain('No results match these filters');
    const clear = root.querySelector<HTMLButtonElement>('app-empty-pattern button')!;
    expect(clear.textContent).toContain('Clear filters');
    clear.click();
    await fixture.whenStable();
    expect(root.textContent).toContain('No activity recorded');
  });
  it('renders the heading as an h2 under the Settings shell and as the page h1 as a sidebar entry', async () => {
    const tab = await render();
    expect(tab.root.querySelectorAll('h1')).toHaveLength(0);
    expect(tab.root.querySelector('h2')?.textContent).toContain('Audit log');
    TestBed.resetTestingModule();
    routeData = { pageHeadingLevel: 1 };
    TestBed.configureTestingModule({ imports: [AuditPage], providers: providers() });
    const entryPage = await render();
    expect(entryPage.root.querySelectorAll('h1')).toHaveLength(1);
    expect(entryPage.root.querySelector('h1')?.textContent).toContain('Audit log');
    expect(entryPage.root.querySelectorAll('h2')).toHaveLength(0);
  });
  it('bounds semantic rows to 200 and moves windows with heading focus', async () => {
    const entries = Array.from({ length: 201 }, (_, index) => entry(`row-${index}`));
    data.list.mockImplementation((_org, query, cursor) => {
      const start = Number(cursor ?? 0);
      const end = Math.min(start + (query.limit ?? 50), entries.length);
      return Promise.resolve({
        items: entries.slice(start, end),
        next_cursor: end < entries.length ? String(end) : null,
      });
    });
    const { fixture, root } = await render();
    expect(data.list).toHaveBeenCalledTimes(1);
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(50);
    for (let page = 0; page < 4; page++) {
      root.querySelector<HTMLButtonElement>('[data-audit-load-more]')!.click();
      await fixture.whenStable();
    }
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(200);
    root.querySelector<HTMLButtonElement>('[data-audit-next]')!.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(1);
    expect(document.activeElement).toBe(root.querySelector('h2'));
    root.querySelector<HTMLButtonElement>('[data-audit-previous]')!.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(200);
  });
  it('stops offering more rows at the list bound and points at the filter', async () => {
    // 20-row pages: the bound is a number of PAGES, so it is reached with 4,000 rows, not 10,000,
    // and the hint must print the rows on screen rather than a fixed product of the two limits.
    const entries = Array.from({ length: 4_500 }, (_, index) => entry(`row-${index}`));
    data.list.mockImplementation((_org, _query, cursor) => {
      const start = Number(cursor ?? 0);
      const end = Math.min(start + 20, entries.length);
      return Promise.resolve({
        items: entries.slice(start, end),
        next_cursor: end < entries.length ? String(end) : null,
      });
    });
    const { fixture, root, store } = await render();
    await store.load('org1');
    for (let page = 1; page < AUDIT_MAX_PAGES; page++) {
      await store.loadMore('org1');
    }
    await fixture.whenStable();
    fixture.detectChanges();

    expect(data.list.mock.calls.every(([, query]) => query.limit === 50)).toBe(true);
    expect(store.rows()).toHaveLength(AUDIT_MAX_PAGES * 20);
    // At the bound the control is gone and the hint says what to do next.
    expect(root.querySelector('[data-audit-load-more]')).toBeNull();
    expect(root.querySelector('[data-audit-browse-capped]')?.textContent).toContain(
      'Showing the newest 4,000 rows. Narrow the filters to review older activity.',
    );
    expect(root.querySelector('[data-audit-paging-error]')).toBeNull();
  });
  it('loads the next cursor only explicitly and preserves rows on partial failure with retry', async () => {
    data.list.mockResolvedValueOnce({ items: [entry('first')], next_cursor: 'page2' });
    const { fixture, root } = await render();
    data.list.mockRejectedValueOnce(new ApiRequestError(refused));
    root.querySelector<HTMLButtonElement>('[data-audit-load-more]')!.click();
    await fixture.whenStable();
    expect(data.list).toHaveBeenLastCalledWith('org1', { limit: 50 }, 'page2');
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(1);
    expect(root.querySelector('[data-audit-paging-error]')?.textContent).toContain(refused.message);
    expect(root.querySelector('[data-audit-paging-error]')?.textContent).toContain(refused.code);
    data.list.mockResolvedValueOnce({ items: [entry('second')], next_cursor: null });
    root.querySelector<HTMLButtonElement>('[data-audit-paging-error] button')!.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(2);
    expect(root.querySelector('[data-audit-paging-error]')).toBeNull();
    expect(root.querySelector('[data-audit-load-more]')).toBeNull();
  });
  it('keeps old activity out of the DOM while organization context is unavailable', async () => {
    const { fixture, root } = await render();
    org.set(null);
    await fixture.whenStable();
    expect(root.querySelector('[data-audit-row]')).toBeNull();
    expect(root.querySelector('app-audit-export')).toBeNull();
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
  });
  it('disables paging during a pending cursor and announces incremental loading', async () => {
    data.list.mockResolvedValueOnce({ items: [entry('first')], next_cursor: 'page2' });
    const { fixture, root, store } = await render();
    const pending = deferred<AuditListResponse>();
    data.list.mockReturnValueOnce(pending.promise);
    const button = root.querySelector<HTMLButtonElement>('[data-audit-load-more]')!;
    button.click();
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    expect(root.querySelector('section[aria-busy]')?.getAttribute('aria-busy')).toBe('true');
    expect(root.textContent).toContain('Loading more activity…');
    pending.resolve({ items: [entry('second')], next_cursor: null });
    await fixture.whenStable();
    // Zoneless: a fake promise settling is not a pending task, so `whenStable()` can resolve with
    // the view still rendered from `paging() === true` — the store has settled, the DOM has not.
    fixture.detectChanges();
    expect(store.paging()).toBe(false);
    expect(root.querySelector('section[aria-busy]')?.getAttribute('aria-busy')).toBe('false');
  });
  it('contains an unexpected paging rejection without rendering internal exception text', async () => {
    data.list.mockResolvedValueOnce({ items: [entry('first')], next_cursor: 'page2' });
    const { fixture, root } = await render();
    data.list.mockRejectedValueOnce(new Error('internal diagnostic'));
    root.querySelector<HTMLButtonElement>('[data-audit-load-more]')!.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(1);
    expect(root.querySelector('[data-audit-paging-error]')?.textContent).toContain(
      'The next audit page could not be loaded. The rows already shown are unchanged.',
    );
    expect(root.textContent).not.toContain('internal diagnostic');
  });
});

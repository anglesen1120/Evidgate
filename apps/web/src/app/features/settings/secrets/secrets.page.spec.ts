import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import fixtureData from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import { SecretsPage } from './secrets.page';
import { SecretsStore, type RotationResult, type RotationState } from './secrets.store';

const base = fixtureData.body.items[0] as IngestTokenView;
const failure: ApiError = {
  kind: 'conflict',
  status: 409,
  code: 'invalid_transition',
  message: 'Already rotated. <strong>Server text stays verbatim.</strong>',
  requestId: 'request-rotate',
  retryable: false,
  retryAfterSeconds: null,
  details: [],
};
class FakeSecretsStore {
  readonly rows = signal<readonly IngestTokenView[]>([base]);
  readonly phase = signal<'loading' | 'ready' | 'error'>('ready');
  readonly loadError = signal<ApiError | null>(null);
  readonly hasMore = signal(false);
  readonly projectId = signal<string | null>(null);
  readonly rotateState = signal<RotationState>({ phase: 'idle', tokenId: null, error: null });
  readonly load = vi.fn((id: string) => {
    this.projectId.set(id);
    return Promise.resolve();
  });
  readonly loadMore = vi.fn(() => Promise.resolve());
  readonly rotate = vi.fn<(id: string) => Promise<RotationResult | null>>(() =>
    Promise.resolve({
      successorId: 'next',
      overlapEndsAt: '2026-10-26T00:00:00Z',
      value: 'new-full-credential-once',
    }),
  );
  resetRotation(): void {
    this.rotateState.set({ phase: 'idle', tokenId: null, error: null });
  }
}
const showModalDescriptor = Object.getOwnPropertyDescriptor(
  HTMLDialogElement.prototype,
  'showModal',
);
const closeDescriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');

describe('SecretsPage (09.7.1-T3)', () => {
  let store: FakeSecretsStore;
  const projects = signal([
    { id: 'p1', name: 'Storefront' },
    { id: 'p2', name: 'API' },
  ]);
  const canWrite = signal(true);
  beforeEach(() => {
    store = new FakeSecretsStore();
    projects.set([
      { id: 'p1', name: 'Storefront' },
      { id: 'p2', name: 'API' },
    ]);
    canWrite.set(true);
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true;
      },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false;
      },
    });
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SessionStore,
          useValue: {
            projects,
            has: (code: string) => code === 'ingest_token:write' && canWrite(),
          },
        },
      ],
    });
    TestBed.overrideComponent(SecretsPage, {
      set: { providers: [{ provide: SecretsStore, useValue: store }, RevealSlot] },
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    if (showModalDescriptor)
      Object.defineProperty(HTMLDialogElement.prototype, 'showModal', showModalDescriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    if (closeDescriptor)
      Object.defineProperty(HTMLDialogElement.prototype, 'close', closeDescriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    vi.restoreAllMocks();
  });
  async function render() {
    const fixture = TestBed.createComponent(SecretsPage);
    fixture.detectChanges();
    await fixture.whenStable();
    return {
      fixture,
      root: fixture.nativeElement as HTMLElement,
      slot: fixture.debugElement.injector.get(RevealSlot),
    };
  }
  function rotateButton(root: HTMLElement): HTMLButtonElement {
    const button = root.querySelector<HTMLButtonElement>('[data-rotate]');
    if (!button) throw new Error('Expected Rotate button');
    return button;
  }
  it('defaults to the first listed project and never requests an injected unlisted id', async () => {
    const { root, fixture } = await render();
    expect(store.load).toHaveBeenCalledExactlyOnceWith('p1');
    const select = root.querySelector<HTMLSelectElement>('select');
    if (!select) throw new Error('Expected project select');
    const option = document.createElement('option');
    option.value = 'foreign';
    select.append(option);
    select.value = 'foreign';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(store.load).toHaveBeenCalledTimes(1);
  });
  it('renders zero projects as empty without requesting any project', async () => {
    projects.set([]);
    const { root } = await render();
    expect(store.load).not.toHaveBeenCalled();
    expect(root.textContent).toContain('No projects available');
    expect(root.querySelector('[data-secret-reference]')).toBeNull();
  });
  it('renders 20 stub-store rows with decoy credentials without leaking any full value to outerHTML', async () => {
    const rows = Array.from({ length: 20 }, (_, index) => ({
      ...base,
      id: `row-${index}`,
      name: `Reference ${index}`,
      prefix: `eg_ci_${index}`,
      secret: `NEVER_RENDER_${index}_${'x'.repeat(64)}`,
    }));
    store.rows.set(rows);
    const { root } = await render();
    expect(root.querySelectorAll('[data-secret-reference]')).toHaveLength(20);
    for (const row of rows) {
      expect(root.outerHTML).not.toContain(row.secret);
      expect(root.textContent).toContain(row.name);
      expect(root.textContent).toContain(row.prefix);
    }
  });
  it('keeps Rotate absent without write permission', async () => {
    canWrite.set(false);
    const { root } = await render();
    expect(root.querySelector('[data-rotate]')).toBeNull();
  });
  it.each(['dismiss', 'escape', 'project', 'destroy'])(
    'clears the one-time value on %s, including the DOM input',
    async (action) => {
      const { fixture, root, slot } = await render();
      const opener = rotateButton(root);
      opener.click();
      await fixture.whenStable();
      const input = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
      expect(input?.value).toBe('new-full-credential-once');
      expect(slot.current()?.source).toBe('ingest-token-rotation');
      expect(root.querySelector('[data-testid="reveal-copy"]')).not.toBeNull();
      expect(root.textContent).toContain('2026-10-26T00:00:00Z');
      if (action === 'dismiss')
        root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
      if (action === 'escape')
        root.querySelector('dialog')?.dispatchEvent(new Event('cancel', { cancelable: true }));
      if (action === 'project') {
        const select = root.querySelector<HTMLSelectElement>('select');
        if (!select) throw new Error('Expected select');
        select.value = 'p2';
        select.dispatchEvent(new Event('change'));
      }
      if (action === 'destroy') fixture.destroy();
      else {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      expect(slot.current()).toBeNull();
      expect(input?.value).toBe('');
      expect(root.outerHTML).not.toContain('new-full-credential-once');
      expect(
        JSON.stringify({
          rows: store.rows(),
          phase: store.phase(),
          rotateState: store.rotateState(),
        }),
      ).not.toContain('new-full-credential-once');
      if (action === 'dismiss' || action === 'escape') expect(document.activeElement).toBe(opener);
      if (action === 'project') expect(store.load).toHaveBeenLastCalledWith('p2');
    },
  );
  it('guards a double click and disables / marks the pending button busy', async () => {
    let settle: ((result: RotationResult | null) => void) | undefined;
    store.rotate.mockImplementation(
      () =>
        new Promise((resolve) => {
          settle = resolve;
        }),
    );
    const { root, fixture } = await render();
    const opener = rotateButton(root);
    opener.click();
    opener.click();
    fixture.detectChanges();
    expect(store.rotate).toHaveBeenCalledTimes(1);
    expect(opener.disabled).toBe(true);
    expect(opener.getAttribute('aria-busy')).toBe('true');
    settle?.(null);
    // The click handler's promise is not an Angular pending task. Flush its continuation before
    // asking zoneless change detection to settle the enabled state.
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(opener.disabled).toBe(false);
  });
  it('drops an in-flight response when the project changes', async () => {
    let settle: ((result: RotationResult | null) => void) | undefined;
    store.rotate.mockImplementation(
      () =>
        new Promise((resolve) => {
          settle = resolve;
        }),
    );
    const { root, fixture, slot } = await render();
    rotateButton(root).click();
    const select = root.querySelector<HTMLSelectElement>('select');
    if (!select) throw new Error('Expected select');
    select.value = 'p2';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    settle?.({ successorId: 'next', overlapEndsAt: null, value: 'abandoned-value' });
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    expect(root.outerHTML).not.toContain('abandoned-value');
  });
  it('renders 409 invalid_transition and its message verbatim without altering the rows', async () => {
    store.rotate.mockImplementation(() => {
      store.rotateState.set({ phase: 'failed', tokenId: base.id, error: failure });
      return Promise.resolve(null);
    });
    const original = store.rows();
    const { root, fixture } = await render();
    rotateButton(root).click();
    await fixture.whenStable();
    expect(root.textContent).toContain(failure.message);
    expect(root.textContent).toContain('invalid_transition');
    expect(root.querySelector('strong')).toBeNull();
    expect(store.rows()).toBe(original);
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
  });
  it('honors a rotation Retry-After without automatic retry or changing the list', async () => {
    const limited: ApiError = {
      ...failure,
      kind: 'rate_limited',
      status: 429,
      code: 'rate_limited',
      message: 'Rotation burst limit reached.',
      retryable: true,
      retryAfterSeconds: 30,
    };
    store.rotate.mockImplementation(() => {
      store.rotateState.set({ phase: 'failed', tokenId: base.id, error: limited });
      return Promise.resolve(null);
    });
    const original = store.rows();
    const { root, fixture } = await render();
    rotateButton(root).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root.textContent).toContain(limited.message);
    expect(root.querySelector('app-retry-after')).not.toBeNull();
    expect(rotateButton(root).disabled).toBe(true);
    rotateButton(root).click();
    expect(store.rotate).toHaveBeenCalledTimes(1);
    expect(store.rows()).toBe(original);
  });
  it.each(['loading', 'empty', 'error', '429', '403', '404', '503', '401'])(
    'renders the %s state',
    async (state) => {
      if (state === 'loading') store.phase.set('loading');
      if (state === 'empty' || state === 'loading') store.rows.set([]);
      if (!['loading', 'empty'].includes(state)) {
        store.phase.set('error');
        store.rows.set([]);
        const kind =
          state === '429'
            ? 'rate_limited'
            : state === '403'
              ? 'forbidden'
              : state === '404'
                ? 'not_found'
                : state === '503'
                  ? 'unavailable'
                  : state === '401'
                    ? 'unauthenticated'
                    : 'unexpected';
        store.loadError.set({
          ...failure,
          kind,
          status: Number(state) || 500,
          message: 'Server read refusal verbatim.',
          retryable: true,
          retryAfterSeconds: state === '429' ? 7 : null,
        });
      }
      const { root } = await render();
      if (state === 'loading') expect(root.querySelector('app-loading-pattern')).not.toBeNull();
      else if (state === 'empty') expect(root.textContent).toContain('No secret references');
      else {
        expect(root.textContent).toContain('Server read refusal verbatim.');
        if (state === '429') {
          expect(root.querySelector('app-retry-after')).not.toBeNull();
          expect(store.load).toHaveBeenCalledTimes(1);
        }
      }
    },
  );
  it('follows one cursor page per Load more and bounds the rendered rows to 200', async () => {
    store.rows.set(Array.from({ length: 200 }, (_, index) => ({ ...base, id: `row${index}` })));
    store.hasMore.set(true);
    store.loadMore.mockImplementation(() => {
      store.rows.update((rows) => [...rows, { ...base, id: 'next' }]);
      return Promise.resolve();
    });
    const { root, fixture } = await render();
    root.querySelector<HTMLButtonElement>('[data-load-more]')?.click();
    await fixture.whenStable();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
    expect(root.querySelectorAll('[data-secret-reference]')).toHaveLength(1);
  });
});

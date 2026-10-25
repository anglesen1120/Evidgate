// QA2 independent gate (09.7.1-T3 AC3 + S82 P1/P5): the sinks the review packet names but the
// suite's own page spec does not exercise — console output and web storage during a rotation — plus
// two interleavings: a second row pressed while one rotation is out, and a throttled next page.
// Real `RevealSlot`, so "cleared on dismiss" is the component's own behaviour.
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
  message: 'Already rotated.',
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
  readonly rotating = signal(false);
  readonly load = vi.fn((id: string) => {
    this.projectId.set(id);
    return Promise.resolve();
  });
  readonly loadMore = vi.fn(() => Promise.resolve());
  readonly rotate = vi.fn<(id: string) => Promise<RotationResult | null>>(() =>
    Promise.resolve(null),
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

/** Every key/value a web storage holds, so a write of any name is visible. */
function dumpStorage(storage: Storage): string {
  const entries: string[] = [];
  for (let index = 0; index < storage.length; index += 1) {
    const key = storage.key(index);
    if (key !== null) entries.push(`${key}=${storage.getItem(key) ?? ''}`);
  }
  return entries.join('&');
}

describe('SecretsPage sinks and interleavings (09.7.1-T3 QA2)', () => {
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
    window.localStorage.clear();
    window.sessionStorage.clear();
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

  function rotateButtons(root: HTMLElement): HTMLButtonElement[] {
    return Array.from(root.querySelectorAll<HTMLButtonElement>('[data-rotate]'));
  }

  it('keeps a full-length rotated credential out of console, web storage, live regions and the DOM after dismiss', async () => {
    const secret = `egt_QA2_${'q'.repeat(37)}_not_a_real_credential`;
    store.rotate.mockImplementation(() =>
      Promise.resolve({ successorId: 'next', overlapEndsAt: null, value: secret }),
    );
    const consoleSpies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((method) =>
      vi.spyOn(console, method),
    );
    const setItem = vi.spyOn(Storage.prototype, 'setItem');

    const { fixture, root, slot } = await render();
    rotateButtons(root)[0]?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    // The value reaches exactly one place: the reveal slot of the mounted page.
    expect(slot.current()).toEqual({ source: 'ingest-token-rotation', value: secret });

    root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(slot.current()).toBeNull();
    expect(root.outerHTML).not.toContain(secret);
    for (const element of root.querySelectorAll('[role="status"], [role="alert"]')) {
      expect(element.textContent ?? '').not.toContain(secret);
    }
    for (const spy of consoleSpies) {
      for (const call of spy.mock.calls) expect(JSON.stringify(call)).not.toContain(secret);
    }
    for (const call of setItem.mock.calls) expect(JSON.stringify(call)).not.toContain(secret);
    expect(dumpStorage(window.localStorage)).not.toContain(secret);
    expect(dumpStorage(window.sessionStorage)).not.toContain(secret);
  });

  it('renders a 409 token_revoked with the server’s own code, not the sibling invalid_transition', async () => {
    const revoked: ApiError = {
      ...failure,
      code: 'token_revoked',
      message: 'The credential was revoked. <em>Verbose server text.</em>',
    };
    store.rotate.mockImplementation(() => {
      store.rotateState.set({ phase: 'failed', tokenId: base.id, error: revoked });
      return Promise.resolve(null);
    });
    const original = store.rows();
    const { fixture, root, slot } = await render();
    rotateButtons(root)[0]?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('[role="alert"] p')?.textContent).toBe(revoked.message);
    expect(root.querySelectorAll('[role="alert"] code')).toHaveLength(1);
    expect(root.querySelector('[role="alert"] code')?.textContent).toBe('token_revoked');
    // The message is interpolated, never parsed: the markup in it created no element.
    expect(root.querySelector('em')).toBeNull();
    expect(slot.current()).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expect(store.rows()).toBe(original);
  });

  it('sends one request when a second row is pressed while a rotation is in flight', async () => {
    store.rows.set([
      { ...base, id: 'row-a' },
      { ...base, id: 'row-b' },
    ]);
    let settle: ((result: RotationResult | null) => void) | undefined;
    store.rotate.mockImplementation(
      () =>
        new Promise<RotationResult | null>((resolve) => {
          settle = resolve;
        }),
    );
    const { fixture, root } = await render();
    const [first, second] = rotateButtons(root);
    if (!first || !second) throw new Error('Expected two Rotate buttons');

    first.click();
    fixture.detectChanges();
    expect(store.rotate).toHaveBeenCalledExactlyOnceWith('row-a');
    // Both rows are locked for the duration of the one request, not only the pressed one.
    expect(first.disabled).toBe(true);
    expect(second.disabled).toBe(true);
    second.click();
    expect(store.rotate).toHaveBeenCalledTimes(1);

    settle?.(null);
    await Promise.resolve();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(store.rotate).toHaveBeenCalledTimes(1);
  });

  it('keeps the shown page and offers the server countdown when the next page is throttled', async () => {
    const rows = Array.from({ length: 200 }, (_, index) => ({ ...base, id: `row-${index}` }));
    store.rows.set(rows);
    store.hasMore.set(true);
    const limited: ApiError = {
      ...failure,
      kind: 'rate_limited',
      status: 429,
      code: 'rate_limited',
      message: 'Too many pages. Retry later.',
      retryable: true,
      retryAfterSeconds: 30,
    };
    store.loadMore.mockImplementation(() => {
      store.loadError.set(limited);
      return Promise.resolve();
    });
    const { fixture, root } = await render();
    const more = root.querySelector<HTMLButtonElement>('[data-load-more]');
    if (!more) throw new Error('Expected Load more');
    more.click();
    await fixture.whenStable();
    fixture.detectChanges();

    // The rows already read stay exactly as served; the refusal is the server's own text.
    expect(store.rows()).toHaveLength(200);
    expect(store.rows()[0]).toBe(rows[0]);
    expect(root.textContent).toContain('Too many pages. Retry later.');
    expect(root.querySelectorAll('app-retry-after')).toHaveLength(1);
    // The countdown owns the retry: pressing again before it elapses asks nothing.
    const again = root.querySelector<HTMLButtonElement>('[data-load-more]');
    expect(again?.disabled).toBe(true);
    again?.click();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
  });
});

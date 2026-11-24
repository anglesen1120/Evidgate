import { InteractivityChecker } from '@angular/cdk/a11y';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CiBindingView } from '@evidgate/sdk';
import bindingData from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { SecretsPage } from './secrets.page';
import { SecretsStore, type RotationResult } from './secrets.store';
import {
  base,
  FakeSecretsStore,
  renderSecretsPage as render,
  confirmRotation,
  SECRET_DESKTOP_PROVIDER,
} from './secrets-page.testing';
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

/** Every key/value a web storage holds, so a write of any name is visible. */
function dumpStorage(storage: Storage): string {
  const entries: string[] = [];
  for (let index = 0; index < storage.length; index += 1) {
    const key = storage.key(index);
    if (key !== null) entries.push(`${key}=${storage.getItem(key) ?? ''}`);
  }
  return entries.join('&');
}

class SecretsInteractionsHarness extends ComponentHarness {
  static hostSelector = 'app-secrets-page';
  readonly tokenNext = this.locatorFor('app-secret-reference-list button[data-page="next"]');
  readonly tokenSummary = this.locatorFor(
    'app-secret-reference-list app-data-table-pager [role="status"]',
  );
  readonly tokenRows = this.locatorForAll('[data-secret-reference]');
  readonly sizes = this.locatorForAll('app-data-table-pager hlm-select-trigger button');
  readonly search = this.locatorFor('#secret-reference-search');
  readonly statusFilters = this.locatorForAll('app-secret-reference-list [role="group"] button');
  readonly clear = this.locatorFor('app-secret-reference-list td[colspan="3"] button');
  readonly inspect = this.locatorFor('[data-secret-inspect]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly dialog = this.documentRootLocatorFactory().locatorFor('[role="dialog"]');
  readonly close = this.documentRootLocatorFactory().locatorFor('[role="dialog"] footer button');
  readonly openDialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');

  async searchReferences(value: string): Promise<void> {
    const search = await this.search();
    await search.setInputValue(value);
    await search.dispatchEvent('input');
  }
}

describe('SecretsPage sinks and interleavings', () => {
  let store: FakeSecretsStore;
  const projects = signal([
    { id: 'p1', name: 'Storefront' },
    { id: 'p2', name: 'API' },
  ]);
  const canWrite = signal(true);
  const canReadBindings = signal(false);

  beforeEach(() => {
    store = new FakeSecretsStore();
    store.ciBindings.set([]);
    store.ciBindingsPhase.set('idle');
    store.rotate.mockResolvedValue(null);
    projects.set([
      { id: 'p1', name: 'Storefront' },
      { id: 'p2', name: 'API' },
    ]);
    canWrite.set(true);
    canReadBindings.set(false);
    window.localStorage.clear();
    window.sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        SECRET_DESKTOP_PROVIDER,
        {
          provide: SessionStore,
          useValue: {
            projects,
            has: (code: string) =>
              (code === 'ingest_token:write' && canWrite()) ||
              (code === 'project:read' && canReadBindings()),
          },
        },
      ],
    });
    TestBed.overrideComponent(SecretsPage, {
      set: { providers: [{ provide: SecretsStore, useValue: store }, RevealSlot] },
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

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
    await confirmRotation(fixture);
    await fixture.whenStable();
    fixture.detectChanges();
    // The value reaches exactly one place: the reveal slot of the mounted page.
    expect(slot.current()).toEqual({ source: 'ingest-token-rotation', value: secret });

    document.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(slot.current()).toBeNull();
    expect(document.body.outerHTML).not.toContain(secret);
    for (const element of document.querySelectorAll('[role="status"], [role="alert"]')) {
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
    await confirmRotation(fixture);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('[role="alert"] p')?.textContent).toBe(revoked.message);
    expect(root.querySelectorAll('[role="alert"] code')).toHaveLength(1);
    expect(root.querySelector('[role="alert"] code')?.textContent).toBe('token_revoked');
    // The message is interpolated, never parsed: the markup in it created no element.
    expect(root.querySelector('em')).toBeNull();
    expect(slot.current()).toBeNull();
    expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
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
    await confirmRotation(fixture);
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
    const rows = Array.from({ length: 20 }, (_, index) => ({ ...base, id: `row-${index}` }));
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
    const page = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      SecretsInteractionsHarness,
    );
    await (await page.tokenNext()).click();
    await fixture.whenStable();
    fixture.detectChanges();

    // The rows already read stay exactly as served; the refusal is the server's own text.
    expect(store.rows()).toHaveLength(20);
    expect(store.rows()[0]).toBe(rows[0]);
    expect(root.textContent).toContain('Too many pages. Retry later.');
    expect(root.querySelectorAll('app-retry-after')).toHaveLength(1);
    // The countdown owns the retry: pressing again before it elapses asks nothing.
    const again = await page.tokenNext();
    expect(await again.getAttribute('aria-disabled')).toBe('true');
    await again.click();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
  });

  it.each([
    { kind: 'tokens', size: 50 },
    { kind: 'tokens', size: 100 },
    { kind: 'bindings', size: 50 },
    { kind: 'bindings', size: 100 },
  ])(
    'changes only the $kind server page size to $size and resets that page',
    async ({ kind, size }) => {
      canReadBindings.set(true);
      store.ciBindings.set(bindingData.body.items as CiBindingView[]);
      store.ciBindingsPhase.set('ready');
      store.page.set(2);
      store.ciBindingsPage.set(3);
      store.setPageSize.mockImplementation(() => {
        store.pageSize.set(size);
        store.page.set(1);
        return Promise.resolve();
      });
      store.setCiBindingsPageSize.mockImplementation(() => {
        store.ciBindingsPageSize.set(size);
        store.ciBindingsPage.set(1);
        return Promise.resolve();
      });
      const { fixture } = await render();
      const page = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        SecretsInteractionsHarness,
      );
      const sizes = await page.sizes();
      expect(sizes).toHaveLength(2);
      const chosen = sizes[kind === 'tokens' ? 0 : 1];
      const untouched = sizes[kind === 'tokens' ? 1 : 0];
      if (!chosen || !untouched)
        throw new Error('Expected independent token and binding size controls');
      expect(await chosen.text()).toBe('20');
      await chosen.click();
      const options = await page.options();
      const labels = await Promise.all(options.map((option) => option.text()));
      expect(labels).toEqual(['20', '50', '100']);
      const option = options[labels.indexOf(String(size))];
      if (!option) throw new Error('Expected requested page size');
      await option.click();
      expect(await chosen.text()).toBe(String(size));
      expect(await untouched.text()).toBe('20');
      const changed = kind === 'tokens' ? store.setPageSize : store.setCiBindingsPageSize;
      const unchanged = kind === 'tokens' ? store.setCiBindingsPageSize : store.setPageSize;
      expect(changed).toHaveBeenCalledExactlyOnceWith(size);
      expect(unchanged).not.toHaveBeenCalled();
      expect(store.page()).toBe(kind === 'tokens' ? 1 : 2);
      expect(store.ciBindingsPage()).toBe(kind === 'bindings' ? 1 : 3);
      expect(store.load).toHaveBeenCalledExactlyOnceWith('p1');
      expect(store.loadCiBindings).toHaveBeenCalledExactlyOnceWith('p1');
      expect(store.loadMore).not.toHaveBeenCalled();
      expect(store.loadMoreCiBindings).not.toHaveBeenCalled();
    },
  );

  it.each(['search', 'status', 'clear'] as const)(
    'resets the token server page on %s filter changes without resetting bindings',
    async (filter) => {
      canReadBindings.set(true);
      store.ciBindings.set(bindingData.body.items as CiBindingView[]);
      store.ciBindingsPhase.set('ready');
      store.ciBindingsPage.set(3);
      store.load.mockImplementation((id) => {
        store.projectId.set(id);
        store.page.set(1);
        return Promise.resolve();
      });
      const { fixture } = await render();
      const page = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        SecretsInteractionsHarness,
      );
      if (filter === 'clear') {
        await page.searchReferences('no-matching-reference');
        expect(await page.tokenRows()).toHaveLength(0);
      }
      expect(store.load).toHaveBeenCalledTimes(1);
      store.page.set(2);
      if (filter === 'search') await page.searchReferences('no-matching-reference');
      else if (filter === 'clear') await (await page.clear()).click();
      else {
        const filters = await page.statusFilters();
        const labels = await Promise.all(filters.map((button) => button.text()));
        const revoked = filters[labels.findIndex((label) => label.startsWith('Revoked'))];
        if (!revoked) throw new Error('Expected Revoked status filter');
        await revoked.click();
      }
      expect(store.load).toHaveBeenNthCalledWith(2, 'p1');
      expect(store.load).toHaveBeenCalledTimes(2);
      expect(await (await page.tokenSummary()).text()).toBe('Page 1');
      expect(store.ciBindingsPage()).toBe(3);
      expect(store.ciBindings()).toEqual(bindingData.body.items);
      expect(store.loadCiBindings).toHaveBeenCalledExactlyOnceWith('p1');
      expect(store.loadMore).not.toHaveBeenCalled();
      expect(store.previous).not.toHaveBeenCalled();
      expect(store.loadMoreCiBindings).not.toHaveBeenCalled();
      expect(store.previousCiBindings).not.toHaveBeenCalled();
      expect(store.setCiBindingsPageSize).not.toHaveBeenCalled();
      expect(await (await page.search()).getProperty('value')).toBe(
        filter === 'search' ? 'no-matching-reference' : '',
      );
      expect(await page.tokenRows()).toHaveLength(filter === 'clear' ? 1 : 0);
    },
  );

  it('opens and closes View on the current token page without changing either cursor', async () => {
    canReadBindings.set(true);
    store.ciBindings.set(bindingData.body.items as CiBindingView[]);
    store.ciBindingsPhase.set('ready');
    store.page.set(2);
    store.ciBindingsPage.set(3);
    const reference = { ...base, id: 'second-page-reference', name: 'Second page reference' };
    store.rows.set([reference]);
    const { fixture } = await render();
    const page = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      SecretsInteractionsHarness,
    );
    const inspect = await page.inspect();
    await inspect.focus();
    await inspect.click();
    expect(await (await page.dialog()).text()).toContain(reference.name);
    expect(store.page()).toBe(2);
    expect(store.ciBindingsPage()).toBe(3);
    await (await page.close()).click();
    expect(await page.openDialog()).toBeNull();
    expect(await inspect.isFocused()).toBe(true);
    expect(await (await page.tokenSummary()).text()).toBe('Page 2');
    expect(await page.tokenRows()).toHaveLength(1);
    expect(store.rows()).toEqual([reference]);
    expect(store.load).toHaveBeenCalledExactlyOnceWith('p1');
    expect(store.loadCiBindings).toHaveBeenCalledExactlyOnceWith('p1');
    expect(store.loadMore).not.toHaveBeenCalled();
    expect(store.previous).not.toHaveBeenCalled();
    expect(store.setPageSize).not.toHaveBeenCalled();
    expect(store.loadMoreCiBindings).not.toHaveBeenCalled();
    expect(store.previousCiBindings).not.toHaveBeenCalled();
    expect(store.setCiBindingsPageSize).not.toHaveBeenCalled();
  });
});

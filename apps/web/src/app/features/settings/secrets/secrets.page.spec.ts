import { InteractivityChecker } from '@angular/cdk/a11y';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { SecretsPage } from './secrets.page';
import { SecretsStore, type RotationResult } from './secrets.store';
import bindingData from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import {
  base,
  failure,
  FakeSecretsStore,
  renderSecretsPage as render,
  rotateButton,
  confirmRotation,
  chooseSecretProject,
  SECRET_DESKTOP_PROVIDER,
} from './secrets-page.testing';
describe('SecretsPage', () => {
  let store: FakeSecretsStore;
  const projects = signal([
    { id: 'p1', name: 'Storefront' },
    { id: 'p2', name: 'API' },
  ]);
  const canWrite = signal(true);
  const canReadBindings = signal(false);
  beforeEach(() => {
    store = new FakeSecretsStore();
    projects.set([
      { id: 'p1', name: 'Storefront' },
      { id: 'p2', name: 'API' },
    ]);
    canWrite.set(true);
    canReadBindings.set(false);
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
  it('defaults to the first listed project and never requests an injected unlisted id', async () => {
    const { root, fixture } = await render();
    expect(store.load).toHaveBeenCalledExactlyOnceWith('p1');
    const select = root.querySelector<HTMLButtonElement>('#secrets-project');
    expect(select?.getAttribute('role')).toBe('combobox');
    expect(root.querySelector('label[for="secrets-project"]')?.textContent).toBe('Project');
    chooseSecretProject(fixture, 'foreign');
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
    await confirmRotation(fixture);
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
    await confirmRotation(fixture);
    chooseSecretProject(fixture, 'p2');
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
    await confirmRotation(fixture);
    await fixture.whenStable();
    expect(root.textContent).toContain(failure.message);
    expect(root.textContent).toContain('invalid_transition');
    expect(root.querySelector('strong')).toBeNull();
    expect(store.rows()).toBe(original);
    expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
  });
  it('names pagination distinctly from the reference list', async () => {
    const { root } = await render();
    expect(root.querySelector('ul')?.getAttribute('aria-label')).toBe('Secret references');
    expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe('Secret references pages');
  });
  it('preserves a pending one-time rotation when list Retry is invoked', async () => {
    let settle: ((result: RotationResult | null) => void) | undefined;
    store.rotate.mockImplementation(() => {
      store.rotating.set(true);
      // The web target's Promise lib predates withResolvers.
      return new Promise<RotationResult | null>((resolve) => {
        settle = resolve;
      });
    });
    const { root, fixture, slot } = await render();
    rotateButton(root).click();
    await confirmRotation(fixture);
    const reset = vi.spyOn(store, 'resetRotation');
    // Exercise the handler even if its visible retry control is disabled.
    fixture.componentInstance['reload']();
    expect(reset).not.toHaveBeenCalled();
    expect(store.load).toHaveBeenCalledTimes(1);
    store.rotating.set(false);
    settle?.({ successorId: 'next', overlapEndsAt: null, value: 'retry-preserved-secret' });
    await fixture.whenStable();
    expect(slot.current()?.value).toBe('retry-preserved-secret');
  });
  it('disables Rotate after a project switch until the store in-flight lock releases', async () => {
    const { root, fixture } = await render();
    store.rotating.set(true);
    chooseSecretProject(fixture, 'p2');
    await fixture.whenStable();
    expect(rotateButton(root).disabled).toBe(true);
    rotateButton(root).click();
    expect(store.rotate).not.toHaveBeenCalled();
    store.rotating.set(false);
    await fixture.whenStable();
    expect(rotateButton(root).disabled).toBe(false);
  });
  it.each([503, 0])(
    'offers list Reload, never a repeat POST, for an ambiguous rotation failure (%s)',
    async (status) => {
      const unavailable: ApiError = {
        ...failure,
        kind: 'unavailable',
        status,
        code: 'unavailable',
        message: 'The rotation response was lost. <em>Server text.</em>',
        retryable: true,
        retryAfterSeconds: null,
      };
      store.rotate.mockImplementation(() => {
        store.rotateState.set({ phase: 'failed', tokenId: base.id, error: unavailable });
        return Promise.resolve(null);
      });
      const { root, fixture, slot } = await render();
      rotateButton(root).click();
      await confirmRotation(fixture);
      await fixture.whenStable();
      expect(root.textContent).toContain(unavailable.message);
      expect(root.textContent).toContain(
        'The rotation may have been applied. Reload the list and check the status before rotating again.',
      );
      expect(root.querySelector('em')).toBeNull();
      expect(root.querySelector('app-retry-after')).toBeNull();
      fixture.componentInstance['retryRotation']();
      expect(store.rotate).toHaveBeenCalledTimes(1);
      const reload = root.querySelector<HTMLButtonElement>('[data-rotation-reload]');
      expect(reload?.textContent?.trim()).toBe('Reload');
      reload?.click();
      await fixture.whenStable();
      expect(store.load).toHaveBeenLastCalledWith('p1');
      expect(store.load).toHaveBeenCalledTimes(2);
      expect(store.rotate).toHaveBeenCalledTimes(1);
      expect(slot.current()).toBeNull();
    },
  );
  it('allows an explicit retry after a refused rate-limited rotation', async () => {
    store.rotate.mockImplementation(() => {
      store.rotateState.set({
        phase: 'failed',
        tokenId: base.id,
        error: {
          ...failure,
          kind: 'rate_limited',
          status: 429,
          retryable: true,
          retryAfterSeconds: 0,
        },
      });
      return Promise.resolve(null);
    });
    const { root, fixture } = await render();
    rotateButton(root).click();
    await confirmRotation(fixture);
    await fixture.whenStable();
    fixture.componentInstance['retryRotation']();
    await confirmRotation(fixture);
    expect(store.rotate).toHaveBeenCalledTimes(2);
    await fixture.whenStable();
  });
  it.each(['read', 'rotation'])(
    'clamps a large %s Retry-After to one day without timer overflow',
    async (source) => {
      const { fixture } = await render();
      const timer = vi.spyOn(globalThis, 'setTimeout');
      const limited: ApiError = {
        ...failure,
        kind: 'rate_limited',
        status: 429,
        retryable: true,
        retryAfterSeconds: 2_147_483_647,
      };
      if (source === 'read') store.loadError.set(limited);
      else store.rotateState.set({ phase: 'failed', tokenId: base.id, error: limited });
      fixture.detectChanges();
      await fixture.whenStable();
      expect(timer).toHaveBeenCalledWith(expect.any(Function), 86_400_000);
      const ready =
        source === 'read'
          ? fixture.componentInstance['readRetryReady']
          : fixture.componentInstance['rotationRetryReady'];
      expect(ready()).toBe(false);
    },
  );
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
    await confirmRotation(fixture);
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
  it('omits CI bindings entirely and never reads them without project:read', async () => {
    const { root } = await render();
    expect(root.querySelector('app-ci-binding-list')).toBeNull();
    expect(store.loadCiBindings).not.toHaveBeenCalled();
  });
  it('reads and renders CI metadata with project:read independently of token write', async () => {
    canReadBindings.set(true);
    canWrite.set(false);
    const { root } = await render();
    expect(store.loadCiBindings).toHaveBeenCalledExactlyOnceWith('p1');
    expect(root.querySelectorAll('[data-ci-binding]')).toHaveLength(bindingData.body.items.length);
    expect(root.querySelector('[data-rotate]')).toBeNull();
  });
  it('reloads CI bindings on project selection and refuses an unlisted injected id', async () => {
    canReadBindings.set(true);
    const { fixture } = await render();
    chooseSecretProject(fixture, 'p2');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(store.loadCiBindings).toHaveBeenNthCalledWith(2, 'p2');
    chooseSecretProject(fixture, 'foreign');
    await fixture.whenStable();
    expect(store.loadCiBindings).toHaveBeenCalledTimes(2);
  });
  it('reads CI bindings once per project scope, ignoring a re-emitted project list', async () => {
    canReadBindings.set(true);
    const { fixture } = await render();
    expect(store.loadCiBindings).toHaveBeenCalledExactlyOnceWith('p1');
    const loads = store.loadCiBindings.mock.calls.length;
    const resets = store.resetCiBindings.mock.calls.length;
    // The session re-emits its project list on unrelated changes: same ids, new array identity.
    projects.set(projects().map((project) => ({ ...project })));
    fixture.detectChanges();
    await fixture.whenStable();
    projects.set([...projects()]);
    fixture.detectChanges();
    await fixture.whenStable();
    // Same scope: no second read, so already loaded cursor pages survive.
    expect(store.loadCiBindings.mock.calls.length).toBe(loads);
    expect(store.resetCiBindings.mock.calls.length).toBe(resets);
    // A permission change is still a scope change: it resets, then reads again.
    canReadBindings.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(store.resetCiBindings.mock.calls.length).toBe(resets + 1);
    canReadBindings.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(store.loadCiBindings.mock.calls.length).toBe(loads + 1);
    expect(store.loadCiBindings).toHaveBeenLastCalledWith('p1');
  });
  it('removes the CI section and resets its state when project:read is revoked', async () => {
    canReadBindings.set(true);
    const { root, fixture } = await render();
    canReadBindings.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('app-ci-binding-list')).toBeNull();
    expect(store.loadCiBindings).toHaveBeenCalledTimes(1);
    expect(store.resetCiBindings).toHaveBeenCalled();
  });
  it('renders a server-forbidden CI section without retry despite project:read', async () => {
    canReadBindings.set(true);
    store.ciBindingsPhase.set('forbidden');
    store.ciBindingsError.set({ ...failure, kind: 'forbidden', status: 403 });
    const { root } = await render();
    expect(root.querySelector('[data-ci-forbidden]')).not.toBeNull();
    expect(root.querySelector('[data-ci-retry]')).toBeNull();
    expect(root.querySelector('[data-ci-binding]')).toBeNull();
  });
  it('loads one further CI page and recovers a rejected cursor read through Retry', async () => {
    canReadBindings.set(true);
    store.hasMoreCiBindings.set(true);
    store.loadMoreCiBindings.mockRejectedValueOnce(new Error('cursor could not be read'));
    const { root, fixture } = await render();
    root.querySelector<HTMLButtonElement>('[data-ci-load-more]')?.click();
    await fixture.whenStable();
    expect(store.loadMoreCiBindings).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-ci-retry]')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('[data-ci-retry]')?.click();
    await fixture.whenStable();
    expect(store.loadCiBindings).toHaveBeenLastCalledWith('p1');
    expect(store.loadCiBindings).toHaveBeenCalledTimes(2);
  });
});

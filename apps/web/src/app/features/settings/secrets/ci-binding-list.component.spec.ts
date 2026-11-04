import { TestBed } from '@angular/core/testing';
import type { CiBindingView } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import bindings from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import { CiBindingListComponent } from './ci-binding-list.component';
import type { CiBindingsPhase } from './secrets.store';

const rows = bindings.body.items as CiBindingView[];
const failure: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'dependency_unavailable',
  message: 'CI metadata is unavailable. <strong>Server text stays verbatim.</strong>',
  requestId: 'request-ci-list',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};

describe('CiBindingListComponent (09.7.1-F15)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(phase: CiBindingsPhase = 'ready', data: readonly CiBindingView[] = rows) {
    const fixture = TestBed.createComponent(CiBindingListComponent);
    fixture.componentRef.setInput('rows', data);
    fixture.componentRef.setInput('phase', phase);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it.each(['idle', 'loading'] as const)('renders %s as an accessible skeleton', async (phase) => {
    const { root } = await render(phase, []);
    expect(root.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading CI bindings');
    expect(root.querySelector('.binding-skeleton')?.getAttribute('aria-hidden')).toBe('true');
    expect(root.querySelectorAll('[data-slot="skeleton"]')).toHaveLength(3);
    expect(root.querySelector('[data-ci-empty]')).toBeNull();
  });

  it('renders the onboarding empty guidance without any mutation control', async () => {
    const { root } = await render('empty', []);
    expect(root.querySelector('[data-ci-empty]')?.textContent).toContain(
      'No CI bindings for this project. Bindings are created during onboarding.',
    );
    expect(root.querySelector('button')).toBeNull();
  });

  it('renders only explicit metadata, all verification labels and the pending hint', async () => {
    const decoy = 'DO_NOT_RENDER_BINDING_CHALLENGE_DECOY';
    const { root } = await render(
      'ready',
      rows.map((row) => ({ ...row, binding_challenge: decoy })),
    );
    expect(root.querySelectorAll('[data-ci-binding]')).toHaveLength(rows.length);
    expect(root.outerHTML).not.toContain(decoy);
    expect(root.outerHTML).not.toContain('binding_challenge');
    expect(root.textContent).toContain('Pending verification');
    expect(root.textContent).toContain('Waiting for the first CI exchange.');
    expect(root.textContent).toContain('Disabled');
    expect(root.querySelector('.status-pill--success')?.textContent).toContain('Active');
    expect(root.textContent).toContain('Not activated');
    for (const row of rows) {
      expect(root.querySelector(`h3[title="${row.repository_full_name}"]`)?.textContent).toContain(
        row.repository_full_name,
      );
      expect(root.textContent).toContain(row.provider);
      expect(root.querySelector(`time[datetime="${row.created_at}"]`)).not.toBeNull();
    }
  });

  it('bounds each allowlist to three entries and toggles independently with keyboard-native buttons', async () => {
    const many = rows.find((row) => row.allowed_ref_patterns.length > 3);
    if (!many) throw new Error('Expected long allowlist fixture');
    const { root, fixture } = await render('ready', [many]);
    const lists = root.querySelectorAll('.patterns');
    const toggles = root.querySelectorAll<HTMLButtonElement>('[data-ci-show-all]');
    expect(lists[0]?.children).toHaveLength(3);
    expect(lists[1]?.children).toHaveLength(3);
    expect(toggles).toHaveLength(2);
    expect(toggles[0]?.getAttribute('data-slot')).toBe('button');
    expect(toggles[0]?.type).toBe('button');
    toggles[0]?.focus();
    expect(document.activeElement).toBe(toggles[0]);
    expect(toggles[0]?.getAttribute('aria-expanded')).toBe('false');
    toggles[0]?.click();
    fixture.detectChanges();
    expect(lists[0]?.children).toHaveLength(many.allowed_ref_patterns.length);
    expect(lists[1]?.children).toHaveLength(3);
    expect(toggles[0]?.getAttribute('aria-expanded')).toBe('true');
    expect(toggles[0]?.textContent).toContain('Show fewer');
    expect(lists[0]?.classList.contains('overflow-y-auto')).toBe(true);
    expect(toggles[0]?.getAttribute('aria-controls')).toBe(lists[0]?.id);
    toggles[0]?.click();
    fixture.detectChanges();
    expect(lists[0]?.children).toHaveLength(3);
    expect(toggles[0]?.getAttribute('aria-expanded')).toBe('false');
  });

  it('handles absent configured patterns without inventing allowlists', async () => {
    const first = rows[0];
    if (!first) throw new Error('Expected binding fixture');
    const { root } = await render('ready', [
      { ...first, allowed_ref_patterns: [], allowed_workflow_refs: [] },
    ]);
    expect(root.querySelectorAll('.patterns li')).toHaveLength(2);
    expect(root.textContent).toContain('None configured');
    expect(root.querySelector('[data-ci-show-all]')).toBeNull();
  });

  it('emits load-more once per click and shows a disabled busy control while loading', async () => {
    const { fixture, root } = await render();
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    const button = root.querySelector<HTMLButtonElement>('[data-ci-load-more]');
    button?.click();
    expect(more).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('loadingMore', true);
    fixture.detectChanges();
    expect(button?.disabled).toBe(true);
    expect(button?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
    expect(button?.textContent).toContain('Loading more CI bindings');
    button?.click();
    expect(more).toHaveBeenCalledTimes(1);
  });

  it('shows typed errors verbatim as text and offers Retry', async () => {
    const { fixture, root } = await render('error', []);
    const retry = vi.fn();
    fixture.componentInstance.retry.subscribe(retry);
    fixture.componentRef.setInput('error', failure);
    fixture.detectChanges();
    expect(root.querySelector('[role="alert"] p')?.textContent).toBe(failure.message);
    expect(root.querySelector('[role="alert"] strong')).toBeNull();
    expect(root.querySelector('[role="alert"] code')?.textContent).toBe(failure.code);
    root.querySelector<HTMLButtonElement>('[data-ci-retry]')?.click();
    expect(retry).toHaveBeenCalledTimes(1);
  });

  it.each(['forbidden', 'ready'] as const)(
    '403 is forbidden even with cached rows (%s), never Retry',
    async (phase) => {
      const { fixture, root } = await render(phase);
      fixture.componentRef.setInput('error', { ...failure, kind: 'forbidden', status: 403 });
      fixture.componentRef.setInput('hasMore', true);
      fixture.detectChanges();
      expect(root.querySelector('[data-ci-forbidden]')?.textContent).toContain('not permitted');
      expect(root.querySelector('[data-ci-binding]')).toBeNull();
      expect(root.querySelector('button')).toBeNull();
    },
  );

  it.each(['unavailable', 'network'] as const)(
    'renders %s with recoverable connection guidance',
    async (kind) => {
      const { fixture, root } = await render(kind === 'network' ? 'error' : 'unavailable', []);
      if (kind === 'network')
        fixture.componentRef.setInput('error', { ...failure, kind: 'network', status: 0 });
      fixture.detectChanges();
      expect(root.querySelector('[role="alert"] p')?.textContent).toContain(
        kind === 'network' ? failure.message : 'Check your connection',
      );
      expect(root.querySelector('[data-ci-retry]')).not.toBeNull();
    },
  );

  it('keeps loaded rows on a paging failure and offers recovery instead of pretending completion', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('pagingFailure', true);
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    expect(root.querySelectorAll('[data-ci-binding]')).toHaveLength(rows.length);
    expect(root.querySelector('[data-ci-retry]')).not.toBeNull();
    expect(root.querySelector('[data-ci-load-more]')).toBeNull();
  });

  it('moves focus to the section heading when the last page removes Load more', async () => {
    const first = rows[0];
    if (!first) throw new Error('Expected binding fixture');
    const { fixture, root } = await render();
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    const more = root.querySelector<HTMLButtonElement>('[data-ci-load-more]');
    if (!more) throw new Error('Expected Load more');
    const emitted = vi.fn();
    fixture.componentInstance.loadMore.subscribe(emitted);
    more.click();
    expect(emitted).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('loadingMore', true);
    fixture.detectChanges();
    // The read settles as the last page: the focused control is gone, so focus must not fall to body.
    fixture.componentRef.setInput('rows', [...rows, { ...first, id: 'appended-binding' }]);
    fixture.componentRef.setInput('hasMore', false);
    fixture.componentRef.setInput('loadingMore', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-ci-load-more]')).toBeNull();
    expect(document.activeElement).toBe(root.querySelector('h2'));
  });

  it('leaves focus on Load more when another page remains', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    const more = root.querySelector<HTMLButtonElement>('[data-ci-load-more]');
    if (!more) throw new Error('Expected Load more');
    more.focus();
    more.click();
    fixture.componentRef.setInput('loadingMore', true);
    fixture.detectChanges();
    fixture.componentRef.setInput('loadingMore', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-ci-load-more]')).toBe(more);
    expect(document.activeElement).toBe(more);
  });

  it('never pulls focus back from an element the user moved to while the page loaded', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    const more = root.querySelector<HTMLButtonElement>('[data-ci-load-more]');
    if (!more) throw new Error('Expected Load more');
    more.click();
    const elsewhere = document.createElement('button');
    document.body.append(elsewhere);
    try {
      // The user tabbed out of the section while the read was in flight.
      elsewhere.focus();
      fixture.componentRef.setInput('loadingMore', true);
      fixture.detectChanges();
      fixture.componentRef.setInput('hasMore', false);
      fixture.componentRef.setInput('loadingMore', false);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(root.querySelector('[data-ci-load-more]')).toBeNull();
      expect(document.activeElement).toBe(elsewhere);
    } finally {
      elsewhere.remove();
    }
  });

  it('moves focus to the section heading when Retry clears the error block', async () => {
    const { fixture, root } = await render('error', []);
    fixture.componentRef.setInput('error', failure);
    fixture.detectChanges();
    const retry = root.querySelector<HTMLButtonElement>('[data-ci-retry]');
    if (!retry) throw new Error('Expected Retry');
    const emitted = vi.fn();
    fixture.componentInstance.retry.subscribe(emitted);
    retry.click();
    expect(emitted).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('loadingMore', true);
    fixture.detectChanges();
    fixture.componentRef.setInput('error', null);
    fixture.componentRef.setInput('phase', 'ready');
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('loadingMore', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-ci-retry]')).toBeNull();
    expect(document.activeElement).toBe(root.querySelector('h2'));
  });

  it.each(['idle', 'loading', 'ready', 'empty', 'forbidden', 'unavailable', 'error'] as const)(
    'keeps the role=status live region mounted in the %s phase',
    async (phase) => {
      const { root } = await render(phase, phase === 'ready' ? rows : []);
      const region = root.querySelector<HTMLElement>('[data-ci-bindings] > p:last-child');
      expect(region?.getAttribute('role')).toBe('status');
      expect(region?.closest('[data-ci-bindings]')).not.toBeNull();
    },
  );
});

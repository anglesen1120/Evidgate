import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CiBindingView } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
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

class BindingPagerHarness extends ComponentHarness {
  static hostSelector = 'app-ci-binding-list';
  readonly next = this.locatorFor('app-data-table-pager button[data-page="next"]');
  readonly previous = this.locatorFor('app-data-table-pager button[data-page="previous"]');
  readonly size = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly summary = this.locatorFor('app-data-table-pager [role="status"]');
  readonly section = this.locatorFor('section');
  readonly retry = this.locatorForOptional('[data-ci-retry]');
  readonly heading = this.locatorFor('h2');
}

provideLayoutTestStubs();

@Component({
  imports: [CiBindingListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-ci-binding-list
      [rows]="rows"
      [phase]="'ready'"
      [hasMore]="hasMore()"
      [loadingMore]="loadingMore()"
      [page]="page()"
      (loadMore)="loadPage()"
    />
  `,
})
class FastPagingHost {
  readonly rows = rows;
  readonly hasMore = signal(true);
  readonly loadingMore = signal(false);
  readonly page = signal(1);

  loadPage(): void {
    this.loadingMore.set(true);
    this.page.set(2);
    this.hasMore.set(false);
    this.loadingMore.set(false);
  }
}

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
    const { root, fixture } = await render('empty', []);
    expect(root.querySelector('[data-ci-empty]')?.textContent).toContain(
      'No CI bindings for this project. Bindings are created during onboarding.',
    );
    const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, BindingPagerHarness);
    expect(await (await pager.size()).getAttribute('role')).toBe('combobox');
    expect(await (await pager.previous()).getAttribute('aria-disabled')).toBe('true');
    expect(await (await pager.next()).getAttribute('aria-disabled')).toBe('true');
    expect(root.querySelector('[data-ci-empty] button')).toBeNull();
    expect(root.querySelector('button:not(app-data-table-pager button)')).toBeNull();
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
      expect(root.querySelector(`time[datetime="${row.created_at}"]`)?.textContent).toContain(
        'UTC',
      );
      if (row.activated_at) {
        expect(root.querySelector(`time[datetime="${row.activated_at}"]`)?.textContent).toContain(
          'UTC',
        );
      }
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

  it('emits one Next intent and keeps both shared pager controls focusable while busy', async () => {
    const { fixture } = await render();
    const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, BindingPagerHarness);
    const more = vi.fn();
    const previous = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    fixture.componentInstance.previous.subscribe(previous);
    fixture.componentRef.setInput('hasMore', true);
    expect(await (await pager.size()).text()).toBe('20');
    expect(await (await pager.previous()).text()).toBe('Previous records');
    await (await pager.previous()).click();
    expect(previous).not.toHaveBeenCalled();
    const next = await pager.next();
    expect(await next.text()).toBe('Next records');
    await next.focus();
    await next.click();
    expect(more).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('loadingMore', true);
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await next.getAttribute('aria-busy')).toBe('true');
    expect(await (await pager.section()).getAttribute('aria-busy')).toBe('true');
    expect(await next.isFocused()).toBe(true);
    await next.click();
    expect(more).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('loadingMore', false);
    fixture.componentRef.setInput('page', 2);
    await (await pager.previous()).click();
    expect(previous).toHaveBeenCalledTimes(1);
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

  it('keeps the current page and Retry on a paging failure without removing the pager', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('pagingFailure', true);
    fixture.componentRef.setInput('hasMore', true);
    fixture.detectChanges();
    const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, BindingPagerHarness);
    expect(root.querySelectorAll('[data-ci-binding]')).toHaveLength(rows.length);
    expect(await pager.retry()).not.toBeNull();
    expect(await (await pager.summary()).text()).toBe('Page 1');
    expect(await (await pager.next()).text()).toBe('Next records');
  });

  it('retains focus on Next when the replacement last page disables it', async () => {
    const first = rows[0];
    if (!first) throw new Error('Expected binding fixture');
    const { fixture } = await render();
    fixture.componentRef.setInput('hasMore', true);
    const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, BindingPagerHarness);
    const next = await pager.next();
    await next.focus();
    await next.click();
    fixture.componentRef.setInput('loadingMore', true);
    fixture.detectChanges();
    fixture.componentRef.setInput('rows', [{ ...first, id: 'replacement-binding' }]);
    fixture.componentRef.setInput('page', 2);
    fixture.componentRef.setInput('hasMore', false);
    fixture.componentRef.setInput('loadingMore', false);
    expect(await (await pager.summary()).text()).toBe('Page 2');
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await next.isFocused()).toBe(true);
    expect(await (await pager.heading()).isFocused()).toBe(false);
  });

  it('keeps focus on Next when a last-page response settles before an intermediate render', async () => {
    const fixture = TestBed.createComponent(FastPagingHost);
    fixture.detectChanges();
    await fixture.whenStable();
    const pager = await TestbedHarnessEnvironment.loader(fixture).getHarness(BindingPagerHarness);
    const next = await pager.next();
    await next.focus();
    await next.click();
    expect(await (await pager.summary()).text()).toBe('Page 2');
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await next.isFocused()).toBe(true);
  });

  it('leaves focus on Next when another page remains', async () => {
    const { fixture } = await render();
    fixture.componentRef.setInput('hasMore', true);
    const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, BindingPagerHarness);
    const next = await pager.next();
    await next.focus();
    await next.click();
    fixture.componentRef.setInput('loadingMore', true);
    fixture.detectChanges();
    fixture.componentRef.setInput('loadingMore', false);
    expect(await next.getAttribute('aria-disabled')).toBe('false');
    expect(await next.isFocused()).toBe(true);
  });

  it('never pulls focus back after the user leaves the pager while the page loads', async () => {
    const { fixture } = await render();
    fixture.componentRef.setInput('hasMore', true);
    const pager = await TestbedHarnessEnvironment.harnessForFixture(fixture, BindingPagerHarness);
    const next = await pager.next();
    await next.focus();
    await next.click();
    const elsewhere = document.createElement('button');
    document.body.append(elsewhere);
    try {
      elsewhere.focus();
      fixture.componentRef.setInput('loadingMore', true);
      fixture.detectChanges();
      fixture.componentRef.setInput('page', 2);
      fixture.componentRef.setInput('hasMore', false);
      fixture.componentRef.setInput('loadingMore', false);
      expect(await next.getAttribute('aria-disabled')).toBe('true');
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

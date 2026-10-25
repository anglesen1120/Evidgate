import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { PolicyRevisionDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { PolicyStore } from './policy.store';
import { RevisionDiffComponent } from './revision-diff.component';

const previous: PolicyRevisionDto = {
  id: 'a',
  project_id: 'project',
  revision_no: 1,
  hash: 'hash-1',
  state: 'retired',
  created_by: 'actor',
  created_at: '2026-10-26T00:00:00Z',
  version: 1,
  body: { required_cases: ['old'], flaky_budget: 1 },
};
const head: PolicyRevisionDto = {
  ...previous,
  id: 'b',
  revision_no: 2,
  state: 'active',
  body: { flaky_budget: 2, critical_cases: ['<img src=x onerror=1>'] },
};
const failure: ApiError = {
  kind: 'network',
  status: 0,
  code: 'network_error',
  message: 'Network <b>unavailable</b>',
  requestId: null,
  details: [],
  retryable: true,
  retryAfterSeconds: null,
};
class FakePolicyStore {
  readonly revisions = signal<readonly PolicyRevisionSummaryDto[]>([previous, head]);
  readonly headRevisionId = signal<string | null>('b');
  readonly loadBody = vi.fn<(id: string) => Promise<PolicyRevisionDto>>((id) =>
    Promise.resolve(id === 'a' ? previous : head),
  );
}
describe('RevisionDiffComponent', () => {
  let store: FakePolicyStore;
  beforeEach(() => {
    store = new FakePolicyStore();
    TestBed.configureTestingModule({ providers: [{ provide: PolicyStore, useValue: store }] });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  /**
   * Flush what the component awaits and re-render. The house `await whenStable(); detectChanges()`
   * is not enough here: the body reads are plain promises the harness does not track, so the state
   * a read resolves (or rejects) to only reaches the DOM after the microtask queue is drained.
   */
  async function settle(fixture: ComponentFixture<RevisionDiffComponent>): Promise<void> {
    await fixture.whenStable();
    for (let round = 0; round < 2; round += 1) {
      // `new Promise` rather than `Promise.withResolvers`: the app compiles with `lib: ES2022`.
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });
      fixture.detectChanges();
    }
  }
  async function render() {
    const fixture = TestBed.createComponent(RevisionDiffComponent);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('defaults to head versus the previous revision and renders all difference kinds as text', async () => {
    const { root } = await render();
    expect(root.querySelector<HTMLSelectElement>('#policy-diff-base')?.value).toBe('a');
    expect(root.querySelector<HTMLSelectElement>('#policy-diff-target')?.value).toBe('b');
    expect(store.loadBody).toHaveBeenCalledWith('a');
    expect(store.loadBody).toHaveBeenCalledWith('b');
    expect(root.querySelector('[data-diff-kind="added"]')?.textContent).toContain(
      '<img src=x onerror=1>',
    );
    expect(root.querySelector('[data-diff-kind="removed"]')?.getAttribute('data-diff-path')).toBe(
      '/required_cases',
    );
    expect(root.querySelector('[data-diff-kind="changed"]')?.getAttribute('data-diff-path')).toBe(
      '/flaky_budget',
    );
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('[onerror]')).toBeNull();
  });
  it('loads selected snapshots and reports target selection', async () => {
    const { fixture, root } = await render();
    const selected = vi.fn();
    fixture.componentInstance.targetChanged.subscribe(selected);
    const select = root.querySelector<HTMLSelectElement>('#policy-diff-target');
    if (!select) throw new Error('Expected target picker');
    select.value = 'a';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(selected).toHaveBeenCalledWith('a');
    expect(store.loadBody).toHaveBeenCalledWith('a');
    expect(root.querySelectorAll('[data-diff-row]')).toHaveLength(0);
    expect(root.querySelector('[data-policy-diff]')?.textContent).toMatch(/\S/);
  });
  it('keeps the pickers on the revision the reader chose and reloads the comparison', async () => {
    const { fixture, root } = await render();
    store.loadBody.mockClear();
    const base = root.querySelector<HTMLSelectElement>('#policy-diff-base');
    if (!base) throw new Error('Expected base picker');
    base.value = 'b';
    base.dispatchEvent(new Event('change'));
    await settle(fixture);
    // The picker really shows the chosen revision (the select was rendered before its options).
    expect(root.querySelector<HTMLSelectElement>('#policy-diff-base')?.value).toBe('b');
    expect(root.querySelector<HTMLSelectElement>('#policy-diff-base')?.selectedIndex).toBe(2);
    // And the comparison reloaded for the new pair.
    expect(store.loadBody).toHaveBeenCalledWith('b');
    expect(root.querySelectorAll('[data-diff-row]')).toHaveLength(0);
  });
  it('shows empty without fetching when fewer than two revisions exist', async () => {
    store.revisions.set([head]);
    const { root } = await render();
    expect(store.loadBody).not.toHaveBeenCalled();
    expect(root.querySelector('[data-policy-diff]')?.textContent).toMatch(/\S/);
  });
  it('shows a failed body read with a manual retry', async () => {
    store.loadBody.mockRejectedValue(new ApiRequestError(failure));
    const { fixture, root } = await render();
    const alert = root.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain(failure.message);
    expect(alert?.querySelector('code')?.textContent).toBe(failure.code);
    // Server text is rendered as text: the message has no markup of its own on the page.
    expect(root.querySelector('b')).toBeNull();
    store.loadBody.mockImplementation((id) => Promise.resolve(id === 'a' ? previous : head));
    root.querySelector<HTMLButtonElement>('button')?.click();
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.querySelectorAll('[data-diff-row]').length).toBeGreaterThan(0);
  });
  it('renders a truncated notice and repeats the array pointer for every added set member', async () => {
    // The real diff, not a stub: 2100 added cases are one row each (set semantics) at one pointer,
    // and the diff's own row cap is what truncates the list.
    const added = Array.from({ length: 2100 }, (_, index) => `case-${index}`);
    store.loadBody.mockImplementation((id) =>
      Promise.resolve(
        id === 'a'
          ? { ...previous, body: { critical_cases: [] } }
          : { ...head, body: { critical_cases: added } },
      ),
    );
    const { root } = await render();
    const rows = root.querySelectorAll('[data-diff-row]');
    expect(rows).toHaveLength(2000);
    expect(rows[0]?.getAttribute('data-diff-path')).toBe('/critical_cases');
    expect(rows[1]?.getAttribute('data-diff-path')).toBe('/critical_cases');
    expect(rows[0]?.getAttribute('data-diff-kind')).toBe('added');
    expect(rows[0]?.textContent).toContain('case-0');
    expect(rows[1]?.textContent).toContain('case-1');
    expect(root.querySelector('[data-policy-diff-truncated]')?.textContent).toMatch(/\S/);
  });
  it('marks loading and ignores a response after destruction', async () => {
    let resolveBody: ((body: PolicyRevisionDto) => void) | undefined;
    store.loadBody.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveBody = resolve;
        }),
    );
    const { fixture, root } = await render();
    expect(root.querySelector('[data-policy-diff]')?.getAttribute('aria-busy')).toBe('true');
    fixture.destroy();
    resolveBody?.(head);
    await Promise.resolve();
    expect(root.querySelectorAll('[data-diff-row]')).toHaveLength(0);
  });
});

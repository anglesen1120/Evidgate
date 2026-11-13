import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { PolicyRevisionDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { MAX_DIFF_VALUE_CHARS } from './policy-diff';
import { rootElement } from './policy-reads.test-support';
import { PolicyStore } from './policy.store';
import { RevisionDiffComponent } from './revision-diff.component';

provideLayoutTestStubs();

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
// Heaviest test here takes ~330 ms on an idle machine; under the smoke container's CPU
// contention (after the fuzz stage) the 5000 ms default was exceeded.
describe('RevisionDiffComponent', { timeout: 30_000 }, () => {
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
    return { fixture, root: rootElement(fixture) };
  }
  async function choose(
    fixture: ComponentFixture<RevisionDiffComponent>,
    root: HTMLElement,
    selector: string,
    label: string,
  ): Promise<void> {
    const trigger = root.querySelector<HTMLButtonElement>(selector);
    if (!trigger) throw new Error('Expected revision picker');
    trigger.click();
    await fixture.whenStable();
    const option = [...document.querySelectorAll<HTMLElement>('[role="option"]')].find(
      (item) => item.textContent?.trim() === label,
    );
    if (!option) throw new Error(`Expected option ${label}`);
    option.click();
    await settle(fixture);
  }
  it('labels its keyboard picker and dismisses it with Escape without changing the comparison', async () => {
    const { fixture, root } = await render();
    const trigger = root.querySelector<HTMLButtonElement>('#policy-diff-target');
    if (!trigger) throw new Error('Expected target picker');
    expect(root.querySelector('label[for="policy-diff-target"]')?.textContent).toContain(
      'Target revision',
    );
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
    expect(trigger.textContent).toContain('Revision 2');
    expect(document.activeElement).toBe(trigger);
  });
  it('defaults to head versus the previous revision and renders all difference kinds as text', async () => {
    const { root } = await render();
    expect(root.querySelector('#policy-diff-base')?.textContent).toContain('Revision 1');
    expect(root.querySelector('#policy-diff-target')?.textContent).toContain('Revision 2');
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
    await choose(fixture, root, '#policy-diff-target', 'Revision 1');
    expect(selected).toHaveBeenCalledWith('a');
    expect(store.loadBody).toHaveBeenCalledWith('a');
    expect(root.querySelectorAll('[data-diff-row]')).toHaveLength(0);
    expect(root.querySelector('[data-policy-diff]')?.textContent).toMatch(/\S/);
  });
  it('keeps the pickers on the revision the reader chose and reloads the comparison', async () => {
    const { fixture, root } = await render();
    store.loadBody.mockClear();
    await choose(fixture, root, '#policy-diff-base', 'Revision 2');
    expect(root.querySelector('#policy-diff-base')?.textContent).toContain('Revision 2');
    expect(store.loadBody).toHaveBeenCalledWith('b');
    expect(root.querySelectorAll('[data-diff-row]')).toHaveLength(0);
  });
  it('shows empty without fetching when fewer than two revisions exist', async () => {
    store.revisions.set([head]);
    const { root } = await render();
    expect(store.loadBody).not.toHaveBeenCalled();
    expect(root.querySelector('[data-policy-diff]')?.textContent).toMatch(/\S/);
    expect(root.querySelector<HTMLButtonElement>('#policy-diff-base')?.disabled).toBe(true);
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
    root.querySelector<HTMLButtonElement>('[data-policy-diff] button')?.click();
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
  it('clips a rendered row label to the same budget as its values, keeping the raw pointer', async () => {
    // Pointer labels use the same display budget as values, without altering the raw path.
    const longKey = 'k'.repeat(MAX_DIFF_VALUE_CHARS * 2);
    store.loadBody.mockImplementation((id) =>
      Promise.resolve(
        id === 'a' ? { ...previous, body: { [longKey]: 1 } } : { ...head, body: { [longKey]: 2 } },
      ),
    );

    const { root } = await render();

    const row = root.querySelector('[data-diff-row]');
    expect(row?.getAttribute('data-diff-path')).toBe(`/${longKey}`);
    const label = row?.querySelector('code')?.textContent ?? '';
    expect(label.length).toBeLessThanOrEqual(MAX_DIFF_VALUE_CHARS);
    expect(label).toContain('…');
    expect(label).not.toContain(longKey);
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

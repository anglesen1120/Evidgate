import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import type {
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { PolicyPage } from './policy.page';
import type { PolicyReadView } from './policy-reads';
import { PolicyStore, type PublishResult } from './policy.store';

provideLayoutTestStubs();

const revision: PolicyRevisionDto = {
  id: '182003de-f396-481b-8578-d18baf086204',
  project_id: 'project',
  revision_no: 2,
  hash: 'abcdef0123456789',
  state: 'active',
  body: { flaky_budget: 1 },
  created_by: 'actor',
  created_at: '2026-10-26T00:00:00Z',
  version: 1,
};

const activation: PolicyActivationStateDto = {
  project_id: 'project',
  policy_revision_id: revision.id,
  hash: revision.hash,
  mode: 'advisory',
  activated_by: 'actor',
  activated_at: '2026-10-26T00:00:00Z',
};

const head: PolicyRevisionSummaryDto = {
  id: revision.id,
  revision_no: revision.revision_no,
  hash: revision.hash,
  state: revision.state,
  created_by: revision.created_by,
  created_at: revision.created_at,
  version: revision.version,
};

class FakePolicyStore {
  readonly phase = signal<'idle' | 'ready' | 'loading' | 'not_found' | 'error'>('ready');
  readonly projectId = signal<string | null>('project');
  readonly revisions = signal<readonly PolicyRevisionSummaryDto[]>([revision]);
  readonly headRevisionId = signal<string | null>(revision.id);
  readonly mode = signal<string | null>(null);
  readonly hasMore = signal(false);
  readonly publishing = signal(false);
  readonly loadError = signal<ApiError | null>(null);
  readonly reads = {
    activation: signal<PolicyReadView<PolicyActivationStateDto>>({
      phase: 'ready',
      value: activation,
      error: null,
    }),
    head: signal<PolicyReadView<PolicyRevisionSummaryDto>>({
      phase: 'ready',
      value: head,
      error: null,
    }),
  };
  readonly open = vi.fn(() => Promise.resolve());
  readonly loadBody = vi.fn(() => Promise.resolve(revision));
  readonly publish = vi.fn<(body: string) => Promise<PublishResult>>(() =>
    Promise.resolve({ kind: 'created', revision }),
  );
  readonly reload = vi.fn(() => Promise.resolve());
  readonly loadMore = vi.fn(() => Promise.resolve());
  readonly reset = vi.fn();
}
describe('PolicyPage', () => {
  let store: FakePolicyStore;
  const canWrite = signal(true);
  const currentOrg = signal<{ id: string } | null>({ id: 'org' });
  const currentProject = signal<{ id: string } | null>(null);
  const projects = signal([{ id: 'project' }]);
  const route = { snapshot: { queryParamMap: convertToParamMap({}) } };
  beforeEach(() => {
    store = new FakePolicyStore();
    canWrite.set(true);
    currentOrg.set({ id: 'org' });
    currentProject.set(null);
    projects.set([{ id: 'project' }]);
    route.snapshot.queryParamMap = convertToParamMap({});
    TestBed.configureTestingModule({
      providers: [
        { provide: ActivatedRoute, useValue: route },
        {
          provide: SessionStore,
          useValue: {
            currentOrg,
            currentProject,
            projects,
            has: (code: string) => code === 'policy:write' && canWrite(),
          },
        },
      ],
    });
    TestBed.overrideComponent(PolicyPage, {
      set: { providers: [{ provide: PolicyStore, useValue: store }] },
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  async function render() {
    const fixture = TestBed.createComponent(PolicyPage);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('uses an h2 heading and shows revision metadata with the activation mode', async () => {
    const { root } = await render();
    expect(root.querySelector('h2#heading')?.textContent).toMatch(/\S/);
    expect(root.querySelector('h1')).toBeNull();
    expect(root.getAttribute('data-policy-phase')).toBe('ready');
    expect(root.querySelector('[data-policy-revision]')?.getAttribute('data-revision-no')).toBe(
      '2',
    );
    expect(root.querySelector('[data-policy-mode]')?.getAttribute('data-mode')).toBe('advisory');
    expect(root.querySelector('[data-policy-publish-open]')).not.toBeNull();
  });
  it('shows the no-activation state without hiding the revisions', async () => {
    store.reads.activation.set({ phase: 'empty', value: null, error: null });
    const { root } = await render();
    expect(root.querySelector('[data-policy-activation-empty]')).not.toBeNull();
    expect(root.querySelectorAll('[data-policy-revision]')).toHaveLength(1);
  });
  it('retries both project reads from the summary retry control', async () => {
    store.reads.head.set({
      phase: 'error',
      value: null,
      error: {
        kind: 'unavailable',
        status: 503,
        code: 'unavailable',
        message: 'a dependency is busy',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: null,
      },
    });
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-policy-reads-retry]')?.click();
    await fixture.whenStable();
    expect(store.reload).toHaveBeenCalledTimes(1);
  });
  it('omits publish control entirely without policy:write', async () => {
    canWrite.set(false);
    const { root } = await render();
    expect(root.querySelector('[data-policy-publish-open]')).toBeNull();
    expect(root.querySelector('[data-policy-publish-dialog]')).toBeNull();
    expect(root.querySelector('#policy-publish-body')).toBeNull();
    expect(root.querySelector('[data-policy-publish-submit]')).toBeNull();
    expect(root.querySelector('app-policy-publish-dialog')).toBeNull();
    expect(store.loadBody).not.toHaveBeenCalled();
  });
  it('forwards initial revision query parameter to the store validator', async () => {
    route.snapshot.queryParamMap = convertToParamMap({ revision: revision.id });
    const { root } = await render();
    expect(root.querySelector<HTMLInputElement>('#policy-revision-id')?.value).toBe(revision.id);
    expect(store.open).toHaveBeenCalledExactlyOnceWith(revision.id);
  });
  it('does not open anything without a revision query parameter', async () => {
    store.phase.set('idle');
    store.revisions.set([]);
    store.headRevisionId.set(null);
    const { root } = await render();
    expect(store.open).not.toHaveBeenCalled();
    expect(store.loadBody).not.toHaveBeenCalled();
    expect(root.querySelector('[hlmAlert]')?.textContent).toContain('No revision selected');
  });
  it('lets the store validate manual revision entry and never puts a policy body in the URL', async () => {
    const { fixture, root } = await render();
    const input = root.querySelector<HTMLInputElement>('#policy-revision-id');
    if (!input) throw new Error('Expected revision entry');
    input.value = 'not-a-uuid';
    input.dispatchEvent(new Event('input'));
    root.querySelector<HTMLButtonElement>('[data-policy-open]')?.click();
    await fixture.whenStable();
    expect(store.open).toHaveBeenCalledExactlyOnceWith('not-a-uuid');
    expect(route.snapshot.queryParamMap.has('body')).toBe(false);
  });
  it('renders a generic error with a retry when the failure carries no typed error', async () => {
    store.phase.set('error');
    store.revisions.set([]);
    const { fixture, root } = await render();
    const alert = root.querySelector('[data-policy-error-generic]');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.textContent).toMatch(/\S/);
    const retry = alert?.querySelector<HTMLButtonElement>('[data-policy-retry]');
    expect(retry).not.toBeNull();
    expect(retry?.textContent).toMatch(/\S/);
    retry?.click();
    await fixture.whenStable();
    expect(store.reload).toHaveBeenCalledTimes(1);
  });
  it('keeps the server error verbatim instead of the generic message when typed', async () => {
    const error: ApiError = {
      kind: 'validation',
      status: 422,
      code: 'policy_revision_refused',
      message: 'Server message verbatim.',
      details: [{ path: '/flaky_budget', reason: 'Out of range.' }],
      requestId: null,
      retryable: false,
      retryAfterSeconds: null,
    };
    store.phase.set('error');
    store.revisions.set([]);
    store.loadError.set(error);
    const { root } = await render();
    expect(root.querySelector('[data-policy-error-generic]')).toBeNull();
    const alert = root.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain(error.code);
    expect(alert?.textContent).toContain(error.message);
    expect(alert?.textContent).toContain('/flaky_budget');
  });
  it.each(['created', 'unchanged'] as const)(
    'announces the revision number after %s and closes the dialog',
    async (kind) => {
      store.publish.mockResolvedValue({ kind, revision });
      const { fixture, root } = await render();
      root.querySelector<HTMLButtonElement>('[data-policy-publish-open]')?.click();
      await fixture.whenStable();
      const body = document.querySelector<HTMLTextAreaElement>('#policy-publish-body');
      expect(body?.value).toBe(JSON.stringify(revision.body, null, 2));
      document.querySelector<HTMLButtonElement>('[data-policy-publish-submit]')?.click();
      await fixture.whenStable();
      const status = root.querySelector('[data-policy-published]');
      expect(status?.textContent).toMatch(/\S/);
      expect(status?.textContent).toContain(String(revision.revision_no));
      expect(status?.closest('[role="status"]')).not.toBeNull();
      expect(document.querySelector('[data-policy-publish-dialog]')).toBeNull();
      expect(store.publish).toHaveBeenCalledTimes(1);
    },
  );
  it.each(['org', 'project', 'membership'])(
    'clears the draft and stale view on %s scope changes',
    async (scope) => {
      store.reset.mockImplementation(() => {
        store.projectId.set(null);
        store.revisions.set([]);
        store.phase.set('idle');
        store.headRevisionId.set(null);
      });
      const { fixture, root } = await render();
      root.querySelector<HTMLButtonElement>('[data-policy-publish-open]')?.click();
      await fixture.whenStable();
      if (scope === 'org') currentOrg.set({ id: 'other-org' });
      if (scope === 'project') currentProject.set({ id: 'other-project' });
      if (scope === 'membership') projects.set([]);
      await fixture.whenStable();
      expect(store.reset).toHaveBeenCalledTimes(1);
      expect(document.querySelector('[data-policy-publish-dialog]')).toBeNull();
      expect(document.querySelector('#policy-publish-body')).toBeNull();
      expect(root.querySelectorAll('[data-policy-revision]')).toHaveLength(0);
      expect(store.publish).not.toHaveBeenCalled();
    },
  );
  it('requests only one additional page per load-more activation', async () => {
    store.hasMore.set(true);
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('app-policy-revision-list button')?.click();
    await fixture.whenStable();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
    fixture.destroy();
    expect(store.reset).toHaveBeenCalledTimes(1);
  });
  it('announces loading, labels the revision field and disables actions until the read completes', async () => {
    store.phase.set('loading');
    store.revisions.set([]);
    const { root } = await render();
    expect(root.querySelector('label[for="policy-revision-id"]')?.textContent).toContain(
      'Revision ID',
    );
    expect(root.querySelector('[role="status"][aria-busy="true"]')?.textContent).toContain(
      'Loading revisions',
    );
    expect(root.querySelector('[hlmSkeleton]')?.getAttribute('aria-hidden')).toBe('true');
    expect(root.querySelector<HTMLInputElement>('#policy-revision-id')?.disabled).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('[data-policy-open]')?.disabled).toBe(true);
    expect(root.querySelector<HTMLButtonElement>('[data-policy-publish-open]')?.disabled).toBe(
      true,
    );
  });
});

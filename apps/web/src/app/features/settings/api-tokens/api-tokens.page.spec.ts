import { TestBed } from '@angular/core/testing';
import type { ApiTokenListResponse, CreateApiTokenRequest } from '@evidgate/sdk';
import { ApiRequestError } from '../../../core/http/api-error';
import { createIntent } from '../../../core/http/intent';
import { CREATED, mount, refusal, ROW, deferred, project } from './api-tokens.test-support';

const BODY: CreateApiTokenRequest = {
  name: 'Reader',
  expires_at: '2027-01-01T00:00:00Z',
  permissions: ['run:read'],
};

describe('ApiTokensPage states', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('reserves a loading panel until the list answers', async () => {
    const { promise: pending, resolve: complete } = deferred<ApiTokenListResponse>();
    const h = mount(() => pending);
    h.fixture.detectChanges();
    expect(h.root.querySelector('app-loading-pattern')).not.toBeNull();
    complete({ items: [ROW], next_cursor: null });
    await h.settle();
    expect(h.root.querySelector('app-api-token-list')).not.toBeNull();
    expect(h.root.querySelector('app-loading-pattern')).toBeNull();
  });

  it('shows an explanatory empty state and a create next step that opens the store', async () => {
    const h = mount(() => Promise.resolve({ items: [], next_cursor: null }));
    await h.settle();
    expect(h.root.textContent).toContain('No API tokens yet');
    expect(h.root.querySelector('table')).toBeNull();
    const button = h.root.querySelector<HTMLButtonElement>('[data-api-create]');
    expect(button?.textContent).toContain('Create token');
    button?.click();
    expect(h.store.createOpen()).toBe(true);
  });

  it('omits mutation buttons for a read-only member', async () => {
    const h = mount(() => Promise.resolve({ items: [], next_cursor: null }));
    h.permissions.set(new Set(['api_token:read']));
    await h.settle();
    expect(h.root.querySelector('[data-api-create]')).toBeNull();
    expect(h.root.textContent).toContain('No API tokens yet');
  });

  it('shows not found for a foreign-tenant forbidden read without exposing metadata', async () => {
    const h = mount(() => Promise.reject(refusal('forbidden')));
    await h.settle();
    expect(h.root.querySelector('app-not-found-pattern')).not.toBeNull();
    expect(h.root.querySelector('app-forbidden-pattern')).toBeNull();
    expect(h.root.querySelector('table')).toBeNull();
    expect(h.root.querySelector('[data-api-create]')).toBeNull();
  });

  it('renders a retryable failure rather than an empty table', async () => {
    const h = mount(() => Promise.reject(refusal('network')));
    await h.settle();
    expect(h.root.querySelector('app-error-pattern')).not.toBeNull();
    expect(h.root.querySelector('app-empty-pattern')).toBeNull();
  });

  it('shows the server Retry-After value for a rate limit', async () => {
    const h = mount(() => Promise.reject(refusal('rate_limited', 60)));
    await h.settle();
    expect(h.root.querySelector('app-retry-after')?.textContent).toContain('60');
    expect(h.root.querySelector('app-retry-after button')?.hasAttribute('disabled')).toBe(true);
  });

  it('writes the selected project with the cleared page so the URL never pairs it with a stale cursor', async () => {
    const h = mount(undefined, {
      projects: [project('project-1'), project('project-2', 'Other')],
      query: { project: 'project-1', cursor: 'page-2' },
    });
    await h.settle();
    const select = h.root.querySelector<HTMLSelectElement>('#api-tokens-project');
    if (!select) throw new Error('Missing project picker');
    select.value = 'project-2';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await h.settle();
    expect(h.navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-2', cursor: null },
        queryParamsHandling: 'merge',
        // A pick the user made is a place to go back to, so it earns its own history entry.
        replaceUrl: false,
      }),
    );
    expect(h.list).toHaveBeenLastCalledWith('project-2', undefined, expect.any(AbortSignal));
  });

  it('writes the page cursor together with its project', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    const next = h.root.querySelector<HTMLButtonElement>('[data-api-next]');
    if (!next) throw new Error('Missing next-page control');
    next.click();
    await h.settle();
    expect(h.navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: 'next-page' },
        replaceUrl: false,
      }),
    );
  });

  it('returns to the first page as a click of its own, keeping a history entry', async () => {
    const h = mount(undefined, { query: { project: 'project-1', cursor: 'page-2' } });
    await h.settle();
    expect(h.list).toHaveBeenLastCalledWith('project-1', 'page-2', expect.any(AbortSignal));
    const first = h.root.querySelector<HTMLButtonElement>('[data-api-first]');
    if (!first) throw new Error('Missing first-page control');
    first.click();
    await h.settle();
    expect(h.navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        replaceUrl: false,
      }),
    );
  });

  it('never rewrites the URL the store has just read its list from', async () => {
    const h = mount(undefined, { query: { project: 'project-1' } });
    await h.settle();
    expect(h.navigate).not.toHaveBeenCalled();
  });

  it('keeps the next repair in place after a project pick that changed nothing', async () => {
    const h = mount(undefined, {
      projects: [project('project-1'), project('project-2', 'Other')],
      query: { project: 'project-1', cursor: 'page-2' },
    });
    await h.settle();
    const select = h.root.querySelector<HTMLSelectElement>('#api-tokens-project');
    if (!select) throw new Error('Missing project picker');
    select.value = 'project-1';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await h.settle();
    expect(h.navigate).not.toHaveBeenCalled();
    h.store.reloadFirst();
    await h.settle();
    // The ignored pick was no destination, so the cursor repair still replaces the entry.
    expect(h.navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        replaceUrl: true,
      }),
    );
  });

  it('corrects the URL in place when the scope resolves after the page opened', async () => {
    const h = mount(undefined, { projects: [] });
    await h.settle();
    expect(h.navigate).not.toHaveBeenCalled();
    h.projects.set([project('project-1')]);
    await h.settle();
    // Nobody clicked: the late-resolved project is written as a repair of the current entry.
    expect(h.navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        replaceUrl: true,
      }),
    );
  });

  it('writes nothing while the session has resolved another organization than the URL names', async () => {
    const h = mount();
    await h.settle();
    h.org.set({ id: 'org-2' });
    await h.settle();
    // The URL still names the org this page is activated for, so its project deliberately stays.
    expect(h.navigate).not.toHaveBeenCalled();
  });

  it('closes a revoke confirmation when the project selection moves under it', async () => {
    const h = mount(undefined, {
      projects: [project('project-1'), project('project-2', 'Other')],
    });
    await h.settle();
    const trigger = h.root.querySelector<HTMLButtonElement>('[data-api-revoke]');
    if (!trigger) throw new Error('Missing revoke action');
    trigger.click();
    await h.settle();
    expect(h.root.querySelector('app-api-token-revoke-dialog')).not.toBeNull();
    h.projects.set([project('project-2', 'Other')]);
    await h.settle();
    expect(h.root.querySelector('app-api-token-revoke-dialog')).toBeNull();
  });

  it('keeps a revoke confirmation open when the same list is rebuilt with equal values', async () => {
    const h = mount();
    await h.settle();
    const trigger = h.root.querySelector<HTMLButtonElement>('[data-api-revoke]');
    if (!trigger) throw new Error('Missing revoke action');
    trigger.click();
    await h.settle();
    h.projects.set([project('project-1')]);
    await h.settle();
    expect(h.root.querySelector('app-api-token-revoke-dialog')).not.toBeNull();
  });

  it('drops the URL cursor when a create sends the list back to its first page', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    openNextPage(h.root);
    await h.settle();
    expect(h.navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({ queryParams: { project: 'project-1', cursor: 'next-page' } }),
    );
    h.list.mockResolvedValue({ items: [{ ...ROW, id: 'created-row' }], next_cursor: null });
    await expect(h.store.create(BODY, createIntent())).resolves.toEqual(CREATED);
    await h.settle();
    expect(h.navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        // Nothing was clicked: the page repairs the entry the user is on instead of adding one.
        replaceUrl: true,
      }),
    );
  });

  it('drops the URL cursor when an ambiguous create re-reads the list for inspection', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    openNextPage(h.root);
    await h.settle();
    h.list.mockResolvedValue({ items: [ROW], next_cursor: null });
    // The create dialog's "check token list" action runs this before it closes.
    h.store.reloadFirst();
    await h.settle();
    expect(h.navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        replaceUrl: true,
      }),
    );
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal));
  });

  it('drops the URL cursor when a revoke conflict re-reads the first page', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    openNextPage(h.root);
    await h.settle();
    h.revoke.mockRejectedValueOnce(
      new ApiRequestError({
        kind: 'conflict',
        status: 409,
        code: 'already_revoked',
        message: 'This token is already revoked.',
        requestId: 'revocation-race',
        retryable: false,
        retryAfterSeconds: null,
        details: [{ path: 'id', reason: 'already_revoked' }],
      }),
    );
    h.list.mockResolvedValue({ items: [{ ...ROW, status: 'revoked' }], next_cursor: null });
    await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(false);
    await h.settle();
    expect(h.navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { project: 'project-1', cursor: null },
        replaceUrl: true,
      }),
    );
  });
});

function openNextPage(root: HTMLElement): void {
  const next = root.querySelector<HTMLButtonElement>('[data-api-next]');
  if (!next) throw new Error('Missing next-page control');
  next.click();
}

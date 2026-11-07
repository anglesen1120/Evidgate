import { TestBed } from '@angular/core/testing';
import { mount, refusal, ROW, deferred } from './api-tokens.test-support';
import type { ApiTokenListResponse } from '@evidgate/sdk';

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
});

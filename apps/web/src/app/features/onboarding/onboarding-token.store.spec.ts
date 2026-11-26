import { TestBed } from '@angular/core/testing';
import type { IngestTokenListResponse } from '@evidgate/sdk';

import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { apiError } from '../../shared/testing/http-data.testing';
import { OnboardingTokenStore } from './onboarding-token.store';
import { TOKEN } from './onboarding.store.test-support';

describe('optional onboarding token inventory', () => {
  const list = vi.fn<OnboardingDataService['listIngestTokens']>();
  let store: InstanceType<typeof OnboardingTokenStore>;

  beforeEach(() => {
    list.mockReset();
    TestBed.configureTestingModule({
      providers: [
        OnboardingTokenStore,
        { provide: OnboardingDataService, useValue: { listIngestTokens: list } },
      ],
    });
    store = TestBed.inject(OnboardingTokenStore);
  });

  it('reads only on entry and distinguishes a confirmed empty page from a refusal', async () => {
    expect(list).not.toHaveBeenCalled();
    list.mockRejectedValueOnce(apiError('forbidden', 403));
    store.setTokenScope('org', 'project');
    expect(store.tokensRefreshing()).toBe(true);
    await vi.waitFor(() => expect(store.tokensRefreshFailure()).toBe('forbidden'));
    expect(store.tokensLoaded()).toBe(false);
    list.mockResolvedValueOnce({ items: [], next_cursor: null });
    await store.refreshTokens('org', 'project');
    expect(store.tokensLoaded()).toBe(true);
    expect(store.tokens()).toEqual([]);
    expect(store.tokensRefreshFailure()).toBeNull();
    expect(list).toHaveBeenCalledWith('project', { limit: 50 });
  });

  it('retains confirmed metadata on a same-scope refusal but never across scopes', async () => {
    list.mockResolvedValueOnce({ items: [TOKEN], next_cursor: 'more' });
    store.setTokenScope('org', 'project');
    await vi.waitFor(() => expect(store.tokensLoaded()).toBe(true));
    expect(store.tokensTruncated()).toBe(true);
    list.mockRejectedValueOnce(apiError('unavailable', 503));
    await store.refreshTokens('org', 'project');
    expect(store.tokensRefreshFailure()).toBe('unavailable');
    expect(store.tokens()).toEqual([TOKEN]);
    list.mockRejectedValueOnce(apiError('not_found', 404));
    store.setTokenScope('other-org', 'project');
    expect(store.tokens()).toEqual([]);
    expect(store.tokensLoaded()).toBe(false);
    expect(store.tokensTruncated()).toBe(false);
    await vi.waitFor(() => expect(store.tokensRefreshFailure()).toBe('not-found'));
  });

  it('does not publish a superseded read or a read that returns after leaving', async () => {
    const pending: Array<(page: IngestTokenListResponse) => void> = [];
    list.mockImplementation(() => new Promise((resolve) => pending.push(resolve)));
    store.setTokenScope('org', 'project');
    const latest = store.refreshTokens('org', 'project');
    pending[1]?.({ items: [TOKEN], next_cursor: null });
    await latest;
    pending[0]?.({ items: [], next_cursor: null });
    await Promise.resolve();
    expect(store.tokens()).toEqual([TOKEN]);
    const last = store.refreshTokens('org', 'project');
    store.clearTokenScope();
    pending[2]?.({ items: [TOKEN], next_cursor: null });
    await last;
    await store.refreshTokens('org', 'project');
    expect(list).toHaveBeenCalledTimes(3);
    expect(store.tokens()).toEqual([]);
    expect(store.tokensLoaded()).toBe(false);
    expect(store.tokensRefreshing()).toBe(false);
  });
});

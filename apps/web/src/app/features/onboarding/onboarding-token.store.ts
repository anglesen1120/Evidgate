import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import type { IngestTokenView } from '@evidgate/sdk';

import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { failureFor, type OnboardingFailure, type OnboardingScope } from './onboarding.store';

export const TOKEN_LIST_LIMIT = 50;

interface TokenInventory {
  tokens: readonly IngestTokenView[];
  tokensTruncated: boolean;
  tokensLoaded: boolean;
  tokensRefreshing: boolean;
  tokensRefreshFailure: OnboardingFailure;
}

const EMPTY: TokenInventory = {
  tokens: [],
  tokensTruncated: false,
  tokensLoaded: false,
  tokensRefreshing: false,
  tokensRefreshFailure: null,
};

/** Provided by the credential page. An inactive page never probes project tokens. */
export const OnboardingTokenStore = signalStore(
  withState(EMPTY),
  withMethods((store) => {
    const data = inject(OnboardingDataService);
    let scope: OnboardingScope | null = null;
    let sequence = 0;

    async function refreshTokens(orgId: string, projectId: string): Promise<void> {
      if (scope?.orgId !== orgId || scope.projectId !== projectId) return;
      const request = ++sequence;
      patchState(store, { tokensRefreshing: true });
      try {
        const page = await data.listIngestTokens(projectId, { limit: TOKEN_LIST_LIMIT });
        if (sequence !== request) return;
        patchState(store, {
          tokens: page.items,
          tokensTruncated: page.next_cursor !== null,
          tokensLoaded: true,
          tokensRefreshing: false,
          tokensRefreshFailure: null,
        });
      } catch (error) {
        if (sequence !== request) return;
        patchState(store, { tokensRefreshFailure: failureFor(error), tokensRefreshing: false });
      }
    }

    return {
      refreshTokens,
      setTokenScope(orgId: string, projectId: string): void {
        if (scope?.orgId === orgId && scope.projectId === projectId) return;
        scope = { orgId, projectId };
        patchState(store, EMPTY);
        void refreshTokens(orgId, projectId);
      },
      clearTokenScope(): void {
        if (scope === null) return;
        scope = null;
        sequence += 1;
        patchState(store, EMPTY);
      },
    };
  }),
  withHooks({ onDestroy: (store) => store.clearTokenScope() }),
);

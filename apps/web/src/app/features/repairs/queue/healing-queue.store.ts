import { computed } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import {
  CANDIDATES,
  eligibleForReview,
  type QueueCandidateViewModel,
} from './healing-queue.fixture';

export const FILTERS = ['all', 'sla', 'locator', 'wait'] as const;
export type QueueFilter = (typeof FILTERS)[number];
export type QueueDialog = 'bulk' | 'switch' | 'rules' | null;

export const HealingQueueStore = signalStore(
  withState<{
    filter: QueueFilter;
    search: string;
    oldest: boolean;
    checkedIds: readonly string[];
    dialog: QueueDialog;
    confirmed: boolean;
  }>({
    filter: 'all',
    search: '',
    oldest: true,
    checkedIds: ['cand-73', 'cand-77'],
    dialog: null,
    confirmed: false,
  }),
  withComputed((store) => ({
    checked: computed(() =>
      CANDIDATES.filter((row) => eligibleForReview(row) && store.checkedIds().includes(row.id)),
    ),
  })),
  withMethods((store) => ({
    setFilter(filter: QueueFilter): void {
      patchState(store, { filter });
    },
    find(search: string): void {
      patchState(store, { search });
    },
    flipSort(): void {
      patchState(store, { oldest: !store.oldest() });
    },
    clearFilters(): void {
      patchState(store, { filter: 'all', search: '' });
    },
    select(record: QueueCandidateViewModel, checked: boolean): void {
      if (!eligibleForReview(record) || checked === store.checkedIds().includes(record.id)) return;
      patchState(store, {
        checkedIds: checked
          ? [...store.checkedIds(), record.id]
          : store.checkedIds().filter((id) => id !== record.id),
        confirmed: false,
      });
    },
    open(dialog: QueueDialog): void {
      patchState(store, { dialog, confirmed: false });
    },
    confirm(): void {
      if (!store.checked().length) return;
      patchState(store, { dialog: null, confirmed: true });
    },
  })),
);

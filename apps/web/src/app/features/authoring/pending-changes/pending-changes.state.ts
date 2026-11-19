import type { StateSignals, WritableStateSource } from '@ngrx/signals';
import type { ApplyAuthoringChangesResponse, AuthoringChangeListItem } from '@evidgate/sdk';
import type { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import type { ApiError } from '../../../core/http/api-error';
import type { PendingApplyReplay, PendingKeys, PendingRun } from './pending-changes.intent';

export interface PendingConflict {
  readonly code: string;
  readonly pointers: readonly { readonly path: string | null; readonly reason: string }[];
}

export const NO_TITLES: Readonly<Record<string, string>> = {};

export const pendingInitialState = {
  projectId: null as string | null,
  targetId: null as string | null,
  rows: [] as readonly AuthoringChangeListItem[],
  nextCursor: null as string | null,
  queueStatus: 'idle',
  queueError: null as ApiError | null,
  queueStale: false,
  queueLoading: false,
  loadToken: 0,
  selectedIds: [] as readonly string[],
  selectionTouched: false,
  /** The batch ETag the reviewed selection was read at; null until that read, and after it moves. */
  batchEtag: null as string | null,
  previewId: null as string | null,
  localReason: '',
  targetTitles: NO_TITLES,
  applying: false,
  applyError: null as ApiError | null,
  applyRequest: null as PendingApplyReplay | null,
  conflict: null as PendingConflict | null,
  receipt: null as ApplyAuthoringChangesResponse | null,
  discardingId: null as string | null,
  discardError: null as ApiError | null,
  queueing: false,
  enqueueError: null as ApiError | null,
  retryAfterSeconds: null as number | null,
};

export type PendingState = typeof pendingInitialState;

/** The store slice the extracted reads and mutations need, structural so they stay plain functions. */
export type PendingStoreHost = StateSignals<PendingState> &
  WritableStateSource<PendingState> & {
    readonly _data: AuthoringPendingDataService;
    readonly _keys: PendingKeys;
    readonly _run: PendingRun;
    readonly canWrite: () => boolean;
    readonly selectedRows: () => readonly AuthoringChangeListItem[];
  };

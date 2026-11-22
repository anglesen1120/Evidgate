import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';

type PreviewAction = 'approve' | 'reject';
interface PreviewState {
  action: PreviewAction | null;
  reason: string;
  prepared: { action: PreviewAction; reason: string } | null;
}
const initialState: PreviewState = { action: null, reason: '', prepared: null };

export const ApprovalPreviewStore = signalStore(
  withState(initialState),
  withMethods((store) => ({
    open(action: PreviewAction): void {
      patchState(store, { action, reason: '' });
    },
    edit(reason: string): void {
      patchState(store, { reason });
    },
    close(): void {
      patchState(store, { action: null, reason: '' });
    },
    prepare(): void {
      const action = store.action();
      const reason = store.reason().trim();
      if (action && reason)
        patchState(store, { action: null, reason: '', prepared: { action, reason } });
    },
  })),
);

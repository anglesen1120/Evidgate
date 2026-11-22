import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';

export const RevocationPreviewStore = signalStore(
  withState({ requestedOpen: false, dismissed: false, reason: '', prepared: false }),
  withMethods((store) => ({
    open(): void {
      patchState(store, { requestedOpen: true });
    },
    close(): void {
      patchState(store, { requestedOpen: false, dismissed: true });
    },
    edit(reason: string): void {
      patchState(store, { reason });
    },
    prepare(): void {
      if (!store.reason().trim()) return;
      patchState(store, { requestedOpen: false, dismissed: true, prepared: true });
    },
  })),
);

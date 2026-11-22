import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';

export const ArtifactInspectionStore = signalStore(
  withState({ zoom: 100, step: 0 }),
  withMethods((store) => ({
    adjust(delta: number): void {
      patchState(store, { zoom: Math.min(150, Math.max(75, store.zoom() + delta)) });
    },
    reset(): void {
      patchState(store, { zoom: 100, step: 0 });
    },
    select(step: number): void {
      patchState(store, { step });
    },
  })),
);

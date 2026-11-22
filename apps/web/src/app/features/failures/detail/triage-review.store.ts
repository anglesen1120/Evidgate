import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { DETAIL_COPY as c } from './failure-detail.copy';

export const triageLabels = [
  ['product_defect', c.product],
  ['test_drift', c.test],
  ['environment', c.environment],
  ['data_issue', c.data],
  ['unknown', c.unknownLabel],
] as const;
export const owners = [
  ['none', c.none],
  ['minh', 'Minh Trần'],
  ['linh', 'Linh Nguyễn'],
] as const;
export const closureReasons = [
  ['duplicate', c.duplicate],
  ['not-reproducible', c.notRepro],
] as const;
type Preview =
  | { readonly label: string; readonly owner: string; readonly reason: string }
  | { readonly closure: string };
interface TriageDraftViewModel {
  readonly label: string;
  readonly owner: string;
  readonly reason: string;
  readonly closure: string;
  readonly preview: Preview | null;
}

export const TriageReviewStore = signalStore(
  withState<TriageDraftViewModel>({
    label: 'product_defect',
    owner: 'minh',
    reason: 'HTTP 500 · product_defect',
    closure: '',
    preview: null,
  }),
  withMethods((store) => ({
    chooseLabel(value: unknown): void {
      if (triageLabels.some(([id]) => id === value) && typeof value === 'string')
        patchState(store, { label: value });
    },
    chooseOwner(value: unknown): void {
      if (owners.some(([id]) => id === value) && typeof value === 'string')
        patchState(store, { owner: value });
    },
    chooseClosure(value: unknown): void {
      if (closureReasons.some(([id]) => id === value) && typeof value === 'string')
        patchState(store, { closure: value });
    },
    editReason(reason: string): void {
      patchState(store, { reason });
    },
    previewTriage(): void {
      const reason = store.reason().trim();
      const owner = owners.find(([id]) => id === store.owner());
      if (reason && owner)
        patchState(store, { preview: { label: store.label(), owner: owner[1], reason } });
    },
    previewClosure(): void {
      const reason = closureReasons.find(([id]) => id === store.closure());
      if (reason) patchState(store, { preview: { closure: reason[1] } });
    },
  })),
);

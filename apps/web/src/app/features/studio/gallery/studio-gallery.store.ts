import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { type GalleryTemplateViewModel, type TemplateSource } from './studio-gallery.fixture';

type GalleryFilters = {
  query: string;
  source: 'all' | TemplateSource;
  picked: GalleryTemplateViewModel['id'];
};
const initial: GalleryFilters = { query: '', source: 'all', picked: 'checkout' };
export const GalleryReviewStore = signalStore(
  withState(initial),
  withMethods((store) => ({
    search(query: string): void {
      patchState(store, { query });
    },
    changeSource(value: unknown): void {
      if (value === 'all' || value === 'built' || value === 'team')
        patchState(store, { source: value });
    },
    choose(picked: GalleryTemplateViewModel['id']): void {
      patchState(store, { picked });
    },
    reset(): void {
      patchState(store, initial);
    },
    filter(templates: readonly GalleryTemplateViewModel[]): readonly GalleryTemplateViewModel[] {
      const term = store.query().trim().toLocaleLowerCase();
      const source = store.source();
      return templates.filter(
        (item) =>
          (source === 'all' || source === item.source) &&
          (!term ||
            [item.title, item.engine, item.intent, item.assertion].some((text) =>
              text.toLocaleLowerCase().includes(term),
            )),
      );
    },
  })),
);

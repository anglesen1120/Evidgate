import { TestBed } from '@angular/core/testing';
import { galleryFixture } from './studio-gallery.fixture';
import { GalleryReviewStore } from './studio-gallery.store';

describe('GalleryReviewStore', () => {
  it('intersects the source with the query instead of revealing hidden cards', () => {
    const store = TestBed.configureTestingModule({ providers: [GalleryReviewStore] }).inject(
      GalleryReviewStore,
    );
    store.changeSource('team');
    expect(store.filter(galleryFixture.templates).map((item) => item.id)).toEqual(['unit']);
    store.search('order authorization');
    expect(store.filter(galleryFixture.templates)).toEqual([]);
    store.changeSource('built');
    expect(store.filter(galleryFixture.templates).map((item) => item.id)).toEqual(['api']);
    store.reset();
    expect(store.filter(galleryFixture.templates)).toHaveLength(3);
  });
});

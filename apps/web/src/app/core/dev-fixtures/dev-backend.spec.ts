import { TestBed } from '@angular/core/testing';
import { DEFAULT_RETAINED_ROWS_CAP, RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { provideDevBackend } from './dev-backend';

describe('production dev-backend seam', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('installs no retained-row cap override, so the product default answers', () => {
    TestBed.configureTestingModule({ providers: [provideDevBackend()] });

    expect(TestBed.inject(RETAINED_ROWS_CAP)).toBe(DEFAULT_RETAINED_ROWS_CAP);
    expect(DEFAULT_RETAINED_ROWS_CAP).toBe(10_000);
  });
});

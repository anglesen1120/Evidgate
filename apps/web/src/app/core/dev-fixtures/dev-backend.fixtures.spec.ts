import { TestBed } from '@angular/core/testing';
import { DEFAULT_RETAINED_ROWS_CAP, RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { provideDevBackend } from './dev-backend.fixtures';

/** The cap the fixtures build answers with for one page URL; the real `DOCUMENT` carries it. */
function capFor(search: string): number {
  const original = location.href;
  history.replaceState({}, '', `/${search}`);
  try {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideDevBackend()] });
    return TestBed.inject(RETAINED_ROWS_CAP);
  } finally {
    history.replaceState({}, '', original);
    TestBed.resetTestingModule();
  }
}

describe('fixture dev-backend retained-row cap', () => {
  it('seeds the cap the fixture query names, so a cap-path run pages a handful of rows', () => {
    expect(capFor('?fixture.retainedRowsCap=25')).toBe(25);
  });

  it('keeps the product default when the query names no usable cap', () => {
    expect(capFor('?fixture.retainedRowsCap=0')).toBe(DEFAULT_RETAINED_ROWS_CAP);
    expect(capFor('?fixture.variant.listCases=browse-cap')).toBe(DEFAULT_RETAINED_ROWS_CAP);
    expect(capFor('')).toBe(DEFAULT_RETAINED_ROWS_CAP);
  });
});

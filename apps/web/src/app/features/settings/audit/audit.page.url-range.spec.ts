import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, type ParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AuditDataService } from '../../../core/data/audit-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { AuditPage } from './audit.page';
import { AuditStore } from './audit.store';

provideLayoutTestStubs();

describe('AuditPage URL date range', () => {
  const org = signal<{ id: string } | null>({ id: 'org1' });
  const list = vi.fn(() => Promise.resolve({ items: [], next_cursor: null }));
  let query: BehaviorSubject<ParamMap>;

  beforeEach(() => {
    query = new BehaviorSubject(convertToParamMap({}));
    list.mockClear();
    org.set({ id: 'org1' });
    TestBed.configureTestingModule({
      imports: [AuditPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuditDataService, useValue: { list } },
        { provide: SessionStore, useValue: { currentOrg: org } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        {
          provide: ActivatedRoute,
          useFactory: () => ({ queryParamMap: query, snapshot: { data: {} } }),
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each([
    [
      'an unparseable from keeps only the valid to',
      { from: 'not-a-date', to: '2026-10-21T10:30:00Z' },
      '',
      '2026-10-21T10:30:00.000Z',
    ],
    [
      'a reversed pair is dropped like apply() refuses it',
      { from: '2026-10-22T10:30:00Z', to: '2026-10-21T10:30:00Z' },
      '',
      '',
    ],
    [
      'an equal boundary is kept',
      { from: '2026-10-21T10:30:00Z', to: '2026-10-21T10:30:00Z' },
      '2026-10-21T10:30:00.000Z',
      '2026-10-21T10:30:00.000Z',
    ],
  ])('URL date range: %s', async (_name, params, from, to) => {
    query.next(convertToParamMap(params));
    const fixture = TestBed.createComponent(AuditPage);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(AuditStore);
    expect(store.filters()).toMatchObject({ from, to });
  });
});

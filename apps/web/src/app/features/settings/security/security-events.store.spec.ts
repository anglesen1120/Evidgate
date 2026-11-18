import { ApplicationRef, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { SecurityEventList } from '@evidgate/sdk';
import { SecurityEventsDataService } from '../../../core/data/security-events-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { EMPTY_SECURITY_FILTERS, SecurityEventsStore } from './security-events.store';

describe('SecurityEventsStore', () => {
  const list = vi.fn<SecurityEventsDataService['list']>();
  const org = signal<{ id: string } | null>({ id: 'org1' });
  beforeEach(() => {
    org.set({ id: 'org1' });
    list.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        SecurityEventsStore,
        { provide: SecurityEventsDataService, useValue: { list } },
        { provide: SessionStore, useValue: { currentOrg: org } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function settle() {
    TestBed.tick();
    await TestBed.inject(ApplicationRef).whenStable();
  }
  it('narrows every supported query parameter and omits empty filters', async () => {
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 50 }, expect.any(AbortSignal));
    store.apply({
      projectId: 'project1',
      type: 'support_access',
      severity: 'critical',
      from: '2026-01-01T00:00:00Z',
      to: '2026-02-01T00:00:00Z',
    });
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      {
        limit: 50,
        project_id: 'project1',
        type: 'support_access',
        severity: 'critical',
        from: '2026-01-01T00:00:00Z',
        to: '2026-02-01T00:00:00Z',
      },
      expect.any(AbortSignal),
    );
    store.apply(EMPTY_SECURITY_FILTERS);
    await settle();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 50 }, expect.any(AbortSignal));
  });
  it('follows opaque cursors, returns to the previous page and resets on filters', async () => {
    list.mockResolvedValueOnce({ items: [], next_cursor: 'opaque+cursor' });
    const store = TestBed.inject(SecurityEventsStore);
    await settle();
    store.next();
    await settle();
    expect(list).toHaveBeenLastCalledWith(
      'org1',
      { limit: 50, cursor: 'opaque+cursor' },
      expect.any(AbortSignal),
    );
    store.previous();
    await settle();
    expect(list).toHaveBeenLastCalledWith('org1', { limit: 50 }, expect.any(AbortSignal));
    store.apply({ ...EMPTY_SECURITY_FILTERS, severity: 'warning' });
    await settle();
    expect(store.pageIndex()).toBe(0);
  });
  it('never publishes a superseded organization response', async () => {
    let finish: (value: SecurityEventList) => void = () => undefined;
    list.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const store = TestBed.inject(SecurityEventsStore);
    org.set({ id: 'old' });
    TestBed.tick();
    org.set({ id: 'new' });
    await settle();
    finish({ items: [], next_cursor: 'old-cursor' });
    await settle();
    expect(store.nextCursor()).toBeNull();
    expect(store.orgId()).toBe('new');
  });
});

import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectDashboardDataService } from '../../../core/data/project-dashboard-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError } from '../../../core/http/api-error';
import { ProjectDashboardStore } from './project-dashboard.store';

const failure = new ApiRequestError({
  kind: 'unavailable',
  status: 503,
  code: 'busy',
  message: 'Busy',
  requestId: null,
  retryable: true,
  retryAfterSeconds: null,
  details: [],
});
const empty = () => Promise.resolve({ items: [], next_cursor: null });

describe('ProjectDashboardStore', () => {
  const project = signal<{ id: string } | null>({ id: 'p1' });
  const org = signal<{ id: string } | null>({ id: 'o1' });
  const permissions = signal(
    new Set([
      'run:read',
      'decision:read',
      'policy:read',
      'project:read',
      'authoring:read',
      'usage:read',
      'audit:read',
      'healing:triage',
    ]),
  );
  let data: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    project.set({ id: 'p1' });
    org.set({ id: 'o1' });
    permissions.set(
      new Set([
        'run:read',
        'decision:read',
        'policy:read',
        'project:read',
        'authoring:read',
        'usage:read',
        'audit:read',
        'healing:triage',
      ]),
    );
    data = Object.fromEntries(
      ['waivers', 'quarantine', 'tree', 'cases', 'changes', 'audit'].map((name) => [
        name,
        vi.fn(empty),
      ]),
    );
    data['runs'] = vi.fn(() => Promise.resolve({ items: [], complete: true }));
    data['usage'] = vi.fn(() =>
      Promise.resolve({ metrics: [], period: '2026-11', project: null, next_cursor: null }),
    );
    TestBed.configureTestingModule({
      providers: [
        ProjectDashboardStore,
        { provide: ProjectDashboardDataService, useValue: data },
        {
          provide: SessionStore,
          useValue: {
            currentProject: project,
            currentOrg: org,
            has: (code: string) => permissions().has(code),
          },
        },
      ],
    });
  });

  it('loads authorized sources independently and retries only the failed widget', async () => {
    data['waivers']?.mockRejectedValueOnce(failure);
    const store = TestBed.inject(ProjectDashboardStore);
    await settle();
    expect(store.waivers.error()?.kind).toBe('unavailable');
    expect(store.runs.value()?.items).toEqual([]);
    expect(store.audit.value()?.items).toEqual([]);
    store.waivers.reload();
    await settle();
    expect(store.waivers.error()).toBeNull();
    expect(data['runs']).toHaveBeenCalledTimes(1);
    expect(data['waivers']).toHaveBeenCalledTimes(2);
  });

  it('makes no requests without permission and discards data after access is revoked', async () => {
    permissions.set(new Set());
    const store = TestBed.inject(ProjectDashboardStore);
    await settle();
    expect(store.runs.visible()).toBe(false);
    expect(data['runs']).not.toHaveBeenCalled();
    permissions.set(new Set(['run:read']));
    await settle();
    expect(data['runs']).toHaveBeenCalledTimes(1);
    permissions.set(new Set());
    await settle();
    expect(store.runs.value()).toBeUndefined();
    expect(store.runs.visible()).toBe(false);
  });

  it('drops late data from an old project and hides a server-forbidden source', async () => {
    let resolvePending: (value: { items: []; complete: boolean }) => void = () => undefined;
    const pending = new Promise<{ items: []; complete: boolean }>((resolve) => {
      resolvePending = resolve;
    });
    data['runs']?.mockReturnValueOnce(pending);
    data['waivers']?.mockRejectedValue(
      new ApiRequestError({ ...failure.error, kind: 'forbidden', status: 403 }),
    );
    const store = TestBed.inject(ProjectDashboardStore);
    await settle();
    expect(store.waivers.visible()).toBe(false);
    project.set({ id: 'p2' });
    await settle();
    resolvePending({ items: [], complete: false });
    await settle();
    expect(store.runs.value()?.complete).toBe(true);
    expect(data['runs']).toHaveBeenLastCalledWith('p2', expect.any(AbortSignal));
  });
});

async function settle(): Promise<void> {
  TestBed.tick();
  await new Promise<void>((resolve) => queueMicrotask(resolve));
  TestBed.tick();
}

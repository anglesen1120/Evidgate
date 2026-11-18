import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SecurityEventsDataService } from '../data/security-events-data.service';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { provideFixtureBackend } from './fixture-backend';
import { FixtureToggles } from './fixture-toggles';

describe('Security-event fixture decisions through the SDK', () => {
  let service: SecurityEventsDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideApi(`${location.origin}${API_BASE_PATH}`),
        provideFixtureBackend(),
      ],
    });
    service = TestBed.inject(SecurityEventsDataService);
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each([
    'auth_failure',
    'cross_tenant_probe',
    'secret_scanner_alert',
    'kill_switch_used',
    'support_access',
  ] as const)('narrows type %s without mutating corpus data', async (type) => {
    const all = await service.list('org-1', { limit: 50 });
    const page = await service.list('org-1', { limit: 50, type });
    expect(page.items).toEqual(all.items.filter((item) => item.type === type));
    expect(page.items).toHaveLength(1);
    expect(await service.list('org-1', { limit: 50 })).toEqual(all);
  });

  it('intersects project/type/severity with inclusive-from and exclusive-to boundaries', async () => {
    const all = await service.list('org-1', { limit: 50 });
    const target = all.items.find((item) => item.type === 'support_access');
    if (target?.project_id === undefined || target.project_id === null)
      throw new Error('The support fixture needs a project');
    const query = {
      limit: 50,
      project_id: target.project_id,
      type: 'support_access' as const,
      severity: target.severity,
      from: target.created_at,
      to: '2026-11-18T09:02:00Z',
    };
    expect((await service.list('org-1', query)).items).toEqual([target]);
    expect((await service.list('org-1', { ...query, to: target.created_at })).items).toEqual([]);
    expect(
      (await service.list('org-1', { ...query, project_id: 'missing-project' })).items,
    ).toEqual([]);
    expect((await service.list('org-1', { ...query, severity: 'critical' })).items).toEqual([]);
  });

  it('preserves a continuation when filters empty a page and terminates the next page', async () => {
    const first = await service.list('org-1', { limit: 50, type: 'cross_tenant_probe' });
    expect(first.next_cursor).not.toBeNull();
    if (first.next_cursor === null) throw new Error('The first page must continue');
    const next = await service.list('org-1', {
      limit: 50,
      type: 'cross_tenant_probe',
      cursor: first.next_cursor,
    });
    expect(next.items).toEqual([]);
    expect(next.next_cursor).toBeNull();
  });

  it('never turns a filtered 403 into an empty success page', async () => {
    TestBed.inject(FixtureToggles).setVariant('listSecurityEvents', 'forbidden');
    await expect(
      service.list('org-1', { limit: 50, type: 'support_access' }),
    ).rejects.toMatchObject({ error: { kind: 'forbidden' } });
  });
});

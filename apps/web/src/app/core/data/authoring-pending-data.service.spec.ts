import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { API_CLIENT, provideApi } from '../http/api-client';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { AuthoringPendingDataService } from './authoring-pending-data.service';

const BASE = `${location.origin}/v1/projects/project-1/authoring`;
const CHANGE_ID = '018f0000-0000-7000-8000-00000000bd01';
const PAGE = { items: [], next_cursor: null };

describe('AuthoringPendingDataService', () => {
  let service: AuthoringPendingDataService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideApi(`${location.origin}/v1`),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    service = TestBed.inject(AuthoringPendingDataService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('reads the pending queue with the state filter, the page size and the exact signal', async () => {
    const get = vi.spyOn(TestBed.inject(API_CLIENT), 'get');
    const controller = new AbortController();
    const pending = service.listPending('project-1', controller.signal, 'cursor-1');
    const request = await takeRequest(http, `${BASE}/changes`);
    expect(get).toHaveBeenCalledWith(expect.objectContaining({ signal: controller.signal }));
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toContain('state=pending');
    expect(request.request.urlWithParams).toContain('limit=200');
    expect(request.request.urlWithParams).toContain('cursor=cursor-1');
    request.flush(JSON.stringify(PAGE), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(PAGE);
  });

  it('sends the selection as one comma-separated ids value and answers its exact batch ETag', async () => {
    const pending = service.selection('project-1', [CHANGE_ID, 'change-2']);
    const request = await takeRequest(http, `${BASE}/changes`);
    expect(request.request.urlWithParams).toContain('ids=');
    expect(request.request.urlWithParams).not.toContain('state=');
    const ids = new URL(request.request.urlWithParams).searchParams.get('ids');
    expect(ids?.split(',')).toEqual([CHANGE_ID, 'change-2']);
    request.flush(JSON.stringify({ items: [], next_cursor: null }), {
      headers: { ...JSON_HEADERS, ETag: '"batch-etag-1"' },
    });
    await expect(pending).resolves.toEqual({ items: [], batchEtag: '"batch-etag-1"' });
  });

  it('refuses a selection answer that carries no batch ETag', async () => {
    const pending = service.selection('project-1', [CHANGE_ID]);
    const request = await takeRequest(http, `${BASE}/changes`);
    request.flush(JSON.stringify({ items: [], next_cursor: null }), { headers: JSON_HEADERS });
    await expect(pending).rejects.toThrow('batch ETag');
  });

  it('applies the exact batch ETag as If-Match and the intent key as Idempotency-Key', async () => {
    const pending = service.apply('project-1', '"batch-etag-1"', [CHANGE_ID], 'key-1');
    const request = await takeRequest(http, (candidate) =>
      candidate.url.includes('/authoring/changes:apply'),
    );
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('If-Match')).toBe('"batch-etag-1"');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toBe(JSON.stringify({ change_ids: [CHANGE_ID] }));
    request.flush(
      JSON.stringify({
        batch_id: CHANGE_ID,
        applied_change_ids: [CHANGE_ID],
        versions: [],
        lineage: [],
        committed_at: '2026-11-19T00:00:00Z',
      }),
      { headers: JSON_HEADERS },
    );
    await expect(pending).resolves.toMatchObject({ applied_change_ids: [CHANGE_ID] });
  });

  it('discards one change with its own key and no If-Match', async () => {
    const pending = service.discard('project-1', CHANGE_ID, 'discard-key');
    const request = await takeRequest(http, `${BASE}/changes/${CHANGE_ID}/discard`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('discard-key');
    expect(request.request.headers.has('If-Match')).toBe(false);
    request.flush(
      JSON.stringify({
        change_id: CHANGE_ID,
        state: 'discarded',
        discarded_at: '2026-11-19T00:00:00Z',
      }),
      { headers: JSON_HEADERS },
    );
    await expect(pending).resolves.toMatchObject({ state: 'discarded' });
  });

  it('queues one change with its key and the closed change document', async () => {
    const body = {
      target_kind: 'checklist_item' as const,
      target_id: CHANGE_ID,
      change_type: 'comment' as const,
      payload: { body: 'Reviewed' },
    };
    const pending = service.queue('project-1', body, 'queue-key');
    const request = await takeRequest(http, (candidate) =>
      candidate.url.endsWith('/authoring/changes'),
    );
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('queue-key');
    expect(request.request.body).toBe(JSON.stringify(body));
    request.flush(
      JSON.stringify({
        change_id: CHANGE_ID,
        state: 'pending',
        base_version: 1,
        created_at: '2026-11-19T00:00:00Z',
      }),
      { headers: JSON_HEADERS },
    );
    await expect(pending).resolves.toMatchObject({ state: 'pending' });
  });

  it('reads one change and a checklist-fact page', async () => {
    const detail = service.detail('project-1', CHANGE_ID);
    const detailRequest = await takeRequest(http, `${BASE}/changes/${CHANGE_ID}`);
    detailRequest.flush(
      JSON.stringify({
        change_id: CHANGE_ID,
        target_kind: 'checklist_item',
        target_id: CHANGE_ID,
        change_type: 'comment',
        payload: { body: 'Reviewed' },
        state: 'pending',
        source: 'manual',
        base_version: 1,
        author_membership_id: CHANGE_ID,
        created_at: '2026-11-19T00:00:00Z',
        version: 1,
      }),
      { headers: JSON_HEADERS },
    );
    await expect(detail).resolves.toMatchObject({ version: 1 });

    const items = service.checklistItems('project-1', undefined, undefined, {
      state: 'active',
    });
    const request = await takeRequest(http, `${BASE}/checklist-items`);
    expect(request.request.urlWithParams).toContain('limit=200');
    expect(request.request.urlWithParams).toContain('state=active');
    request.flush(JSON.stringify(PAGE), { headers: JSON_HEADERS });
    await expect(items).resolves.toEqual(PAGE);
  });
});

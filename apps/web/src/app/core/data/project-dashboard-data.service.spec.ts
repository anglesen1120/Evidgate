import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { ProjectDashboardDataService } from './project-dashboard-data.service';

const BASE = `${location.origin}${API_BASE_PATH}`;

describe('ProjectDashboardDataService', () => {
  let http: HttpTestingController;
  let data: ProjectDashboardDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(BASE),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    data = TestBed.inject(ProjectDashboardDataService);
  });
  afterEach(() => http.verify());

  it('walks at most five ascending pages of 100 and marks an unfinished sample incomplete', async () => {
    const result = data.runs('p', new AbortController().signal);
    for (let index = 0; index < 5; index++) {
      const request = await takeRequest(http, `${BASE}/projects/p/runs`);
      const query = new URL(request.request.urlWithParams).searchParams;
      expect(query.get('limit')).toBe('100');
      expect(query.get('cursor')).toBe(index === 0 ? null : `c${index}`);
      request.flush(JSON.stringify({ items: [], next_cursor: `c${index + 1}` }), {
        headers: JSON_HEADERS,
      });
    }
    await expect(result).resolves.toEqual({ items: [], complete: false });
  });
  it('stops on a complete page without inventing a continuation or a newest sort parameter', async () => {
    const result = data.runs('p', new AbortController().signal);
    const request = await takeRequest(http, `${BASE}/projects/p/runs`);
    expect(new URL(request.request.urlWithParams).searchParams.has('sort')).toBe(false);
    request.flush(JSON.stringify({ items: [], next_cursor: null }), { headers: JSON_HEADERS });
    await expect(result).resolves.toEqual({ items: [], complete: true });
  });
  it('loads independent widgets in parallel with explicit limits and supported filters', async () => {
    const signal = new AbortController().signal;
    const reads = [
      data.waivers('p', signal),
      data.quarantine('p', signal),
      data.bindings('p', signal),
      data.tree('p', signal),
      data.cases('p', signal),
      data.changes('p', signal),
      data.usage('o', signal),
      data.audit('o', signal),
    ];
    const paths = [
      'projects/p/waivers',
      'projects/p/quarantine',
      'projects/p/ci-bindings',
      'projects/p/authoring/tree',
      'projects/p/authoring/manual-cases',
      'projects/p/authoring/changes',
      'orgs/o/usage/summary',
      'orgs/o/audit',
    ];
    for (const [index, path] of paths.entries()) {
      const request = await takeRequest(http, `${BASE}/${path}`);
      const query = new URL(request.request.urlWithParams).searchParams;
      expect(query.get('limit')).toBe(String([10, 10, 10, 10, 20, 20, 1, 3][index]));
      if (path.endsWith('/changes')) expect(query.get('state')).toBe('pending');
      request.flush(
        JSON.stringify(
          path.endsWith('/summary')
            ? { metrics: [], period: '2026-11', project: null, next_cursor: null }
            : { items: [], next_cursor: null },
        ),
        { headers: JSON_HEADERS },
      );
    }
    await expect(Promise.all(reads)).resolves.toHaveLength(8);
  });
});

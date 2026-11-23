import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectDashboardDataService } from '../../../core/data/project-dashboard-data.service';
import { provideApi } from '../../../core/http/api-client';
import { API_BASE_PATH } from '../../../core/http/api-origin';
import { SessionStore } from '../../../core/session/session.store';
import { JSON_HEADERS, takeRequest } from '../../../shared/testing/http-data.testing';
import { ProjectDashboardStore } from './project-dashboard.store';

const BASE = `${location.origin}${API_BASE_PATH}`;

describe('ProjectDashboard requests', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(BASE),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
        ProjectDashboardDataService,
        ProjectDashboardStore,
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: 'p' }),
            currentOrg: signal({ id: 'o' }),
            has: () => true,
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('loads the real dashboard resources without requesting the unimplemented incidents route', async () => {
    const store = TestBed.inject(ProjectDashboardStore);
    TestBed.tick();
    for (const path of [
      'projects/p/runs',
      'projects/p/waivers',
      'projects/p/quarantine',
      'projects/p/ci-bindings',
      'projects/p/authoring/tree',
      'projects/p/authoring/manual-cases',
      'projects/p/authoring/changes',
      'orgs/o/usage/summary',
      'orgs/o/audit',
    ]) {
      const request = await takeRequest(http, `${BASE}/${path}`);
      request.flush(
        JSON.stringify(
          path.endsWith('/summary')
            ? { metrics: [], period: '2026-11', project: null, next_cursor: null }
            : { items: [], next_cursor: null },
        ),
        { headers: JSON_HEADERS },
      );
    }
    await Promise.resolve();
    TestBed.tick();
    http.expectNone((request) => request.url.includes('/incidents'));
    expect(store.runs.error()).toBeNull();
  });
});

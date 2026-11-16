import { provideZonelessChangeDetection, signal, type Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SessionStore } from '../../core/session/session.store';
import { ProjectOverviewPage } from '../../features/project/overview.page';
import { NavViewService } from '../nav/nav-view';
import { NoOrganizationPage } from './no-organization.page';
import { ServiceUnavailablePage } from './service-unavailable.page';

const pages: readonly Type<unknown>[] = [
  NoOrganizationPage,
  ServiceUnavailablePage,
  ProjectOverviewPage,
];

describe('Shell page heading roles', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(pages)('uses exactly one stable page-sized heading for %s', async (page) => {
    TestBed.configureTestingModule({
      imports: [page],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: SessionStore, useValue: { currentProject: signal(null), reload: vi.fn() } },
        { provide: NavViewService, useValue: { groups: signal([]) } },
      ],
    });
    const fixture = TestBed.createComponent(page);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('h1')?.getAttribute('data-title-size')).toBe('page');
  });
});

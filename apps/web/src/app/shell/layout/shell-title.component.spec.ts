import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { By } from '@angular/platform-browser';

import { SessionStore } from '../../core/session/session.store';
import { QuotaStore } from '../../features/settings/quota-retention/quota.store';
import { routes as suiteRoutes } from '../../features/suites/suites.routes';
import { NavViewService } from '../nav/nav-view';
import { ShellComponent } from './shell.component';
import { stubScopeBrowserApis } from './scope.fixture';

@Component({
  selector: 'app-async-title-test-page',
  template: '<h1>{{ heading() }}</h1>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class AsyncTitleTestPage {
  readonly heading = signal('Loading suite composition');
}

describe('Shell destination title', () => {
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  it('prefers the real localized leaf-route title through async heading changes without stealing focus', async () => {
    const stableTitle = suiteRoutes.find((route) => route.path === 'revisions/:revisionId')?.title;
    if (typeof stableTitle !== 'string')
      throw new Error('The suite destination needs a stable title');
    await TestBed.configureTestingModule({
      imports: [ShellComponent],
      deferBlockBehavior: DeferBlockBehavior.Manual,
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          {
            path: 'suites',
            title: 'Suites',
            children: [
              {
                path: 'revision',
                component: AsyncTitleTestPage,
                title: stableTitle,
              },
            ],
          },
        ]),
        {
          provide: SessionStore,
          useValue: {
            organizations: signal([]),
            projects: signal([]),
            currentOrg: signal(null),
            currentProject: signal(null),
          },
        },
        { provide: NavViewService, useValue: { groups: signal([]) } },
        { provide: QuotaStore, useValue: { badge: signal(null), load: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ShellComponent);
    const root = fixture.nativeElement as HTMLElement;
    document.body.append(root);
    try {
      await fixture.whenStable();
      const toggle = root.querySelector<HTMLButtonElement>('.nav-toggle');
      if (toggle === null) throw new Error('Missing navigation toggle');
      toggle.focus();
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/suites/revision');
      await fixture.whenStable();
      expect(document.title).toBe(`${stableTitle} · Evidgate`);
      expect(root.querySelector('main h1')?.textContent).toContain('Loading suite composition');
      expect(document.activeElement).toBe(toggle);
      const page = fixture.debugElement
        .query(By.directive(AsyncTitleTestPage))
        .injector.get(AsyncTitleTestPage);
      page.heading.set(stableTitle);
      await fixture.whenStable();
      expect(root.querySelector('main h1')?.textContent).toBe(stableTitle);
      expect(document.title).toBe(`${stableTitle} · Evidgate`);
      expect(document.activeElement).toBe(toggle);
      await router.navigateByUrl('/suites/revision?tab=matrix#members');
      await fixture.whenStable();
      expect(document.title).toBe(`${stableTitle} · Evidgate`);
      expect(document.activeElement).toBe(toggle);
    } finally {
      root.remove();
      fixture.destroy();
    }
  });
});

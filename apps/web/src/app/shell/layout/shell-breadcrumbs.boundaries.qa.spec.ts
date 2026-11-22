import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { NAV_ENTRIES } from '../nav/nav-registry';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellBreadcrumbsComponent } from './shell-breadcrumbs.component';

class BreadcrumbBoundaryHarness extends ComponentHarness {
  static hostSelector = 'app-shell-breadcrumbs';
  readonly names = this.locatorForAll('li bdi');
  readonly current = this.locatorForOptional('[aria-current="page"]');
  readonly navigation = this.locatorForOptional('nav');
}

describe('Shell breadcrumbs unknown-route boundaries', () => {
  async function open(path: string, scoped = true) {
    const permissions = signal(new Set<string>(NAV_ENTRIES.map((entry) => entry.permission)));
    TestBed.configureTestingModule({
      imports: [ShellBreadcrumbsComponent],
      providers: [
        provideRouter([{ path: '**', component: NotFoundPage }]),
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal(scoped ? { id: 'o', name: 'Acme' } : null),
            currentProject: signal(scoped ? { id: 'p', name: 'Storefront' } : null),
            has: (code: string) => permissions().has(code),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ShellBreadcrumbsComponent);
    await TestBed.inject(Router).navigateByUrl(path);
    const crumbs = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      BreadcrumbBoundaryHarness,
    );
    return { crumbs, permissions };
  }

  it.each(['unknown/private-case', 'overview-not-a-route'])(
    'does not invent a current screen or echo raw URL text for %s',
    async (path) => {
      const { crumbs } = await open(`/orgs/o/projects/p/${path}?secret=do-not-display#private`);
      expect(await Promise.all((await crumbs.names()).map((part) => part.text()))).toEqual([
        'Acme',
        'Storefront',
      ]);
      expect(await crumbs.current()).toBeNull();
      expect(await (await crumbs.host()).text()).not.toContain('do-not-display');
    },
  );

  it('removes the contextual screen name when its permission disappears', async () => {
    const { crumbs, permissions } = await open('/orgs/o/projects/p/authoring/cases/private-case');
    expect(await (await crumbs.current())?.text()).toBe('Test case editor');
    permissions.set(new Set());
    expect(await Promise.all((await crumbs.names()).map((part) => part.text()))).toEqual([
      'Acme',
      'Storefront',
    ]);
    expect(await crumbs.current()).toBeNull();
  });

  it('renders no empty breadcrumb landmark when no scope or screen is available', async () => {
    const { crumbs } = await open('/unknown', false);
    expect(await crumbs.navigation()).toBeNull();
    expect(await crumbs.names()).toHaveLength(0);
  });
});

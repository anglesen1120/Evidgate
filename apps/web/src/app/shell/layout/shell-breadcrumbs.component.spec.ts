import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { NotFoundPage } from '../pages/not-found.page';
import { ShellBreadcrumbsComponent } from './shell-breadcrumbs.component';

class Breadcrumbs extends ComponentHarness {
  static hostSelector = 'app-shell-breadcrumbs';
  readonly names = this.locatorForAll('li bdi');
  readonly current = this.locatorForOptional('[aria-current="page"]');
}

describe('Shell breadcrumbs', () => {
  async function open(path: string, grants: string[]) {
    const granted = signal(new Set(grants));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'orgs/o/settings/members',
            component: NotFoundPage,
            data: { settingsTabId: 'members' },
          },
          { path: '**', component: NotFoundPage },
        ]),
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'o', name: 'Acme' }),
            currentProject: signal({ id: 'p', name: 'Storefront' }),
            has: (code: string) => granted().has(code),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ShellBreadcrumbsComponent);
    await TestBed.inject(Router).navigateByUrl('/orgs/o/projects/p/' + path);
    const crumbs = await TestbedHarnessEnvironment.harnessForFixture(fixture, Breadcrumbs);
    return { crumbs, granted, fixture };
  }

  it('uses the registered parent and detail label, never the raw case id or query', async () => {
    const { crumbs, fixture } = await open('authoring/cases/record-id?from=list', [
      'authoring:read',
    ]);
    expect(await Promise.all((await crumbs.names()).map((name) => name.text()))).toEqual([
      'Acme',
      'Storefront',
      'Feature tree',
      'Test case editor',
    ]);
    expect(await (await crumbs.current())?.text()).toBe('Test case editor');
    fixture.componentRef.setInput('interactiveScope', true);
    expect(await Promise.all((await crumbs.names()).map((name) => name.text()))).toEqual([
      'Feature tree',
      'Test case editor',
    ]);
    expect(await (await crumbs.current())?.text()).toBe('Test case editor');
  });

  it('names the admitted settings tab even when its standalone entry is not listed', async () => {
    const { crumbs } = await open('overview', ['organization:read']);
    await TestBed.inject(Router).navigateByUrl('/orgs/o/settings/members');
    expect(await (await crumbs.current())?.text()).toBe('Members');
  });

  it('does not disclose an unavailable contextual parent', async () => {
    const { crumbs, granted } = await open('ai-settings', ['authoring:read', 'organization:read']);
    const labels = async () => Promise.all((await crumbs.names()).map((name) => name.text()));
    expect(await labels()).toEqual(['Acme', 'Storefront', 'Settings', 'Project AI settings']);
    granted.set(new Set(['authoring:read']));
    expect(await labels()).toEqual(['Acme', 'Storefront', 'Project AI settings']);
  });
});

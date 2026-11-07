import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import {
  FakeIdentityData,
  NOT_FOUND_MARKER,
  ORG,
  renderedMarker,
  setUp,
  urlOf,
} from './app.routes.test-support';
import type { NavEntry } from './shell/nav/nav.models';
import { NAV_ENTRIES } from './shell/nav/nav-registry';

describe('Settings shell entry override (09.7.1-T1.1)', () => {
  /** The registry row a case names, or a failure naming the missing row. */
  function entryOf(id: string): NavEntry {
    const entry = NAV_ENTRIES.find((candidate) => candidate.id === id);
    if (entry === undefined) {
      throw new Error(`NAV_ENTRIES has no '${id}' entry`);
    }
    return entry;
  }

  it('mounts the Settings shell table for the settings entry, landing on the first available tab', async () => {
    const { harness } = await setUp(
      new FakeIdentityData(),
      new Set(['organization:read', 'usage:read']),
    );
    const router = TestBed.inject(Router);

    await harness.navigateByUrl(urlOf(entryOf('settings')));

    // The caller has the usage summary permission but no project read permission, so the
    // organization quota tab is the first destination it may open.
    expect(router.url).toBe(`/orgs/${ORG.id}/settings/quota-retention`);
    expect(
      document.querySelector('[data-settings-tab-body]')?.getAttribute('data-settings-tab-body'),
    ).toBe('quota-retention');
    expect(document.querySelector('app-route-stub')).toBeNull();
  });

  it('renders not found, never /forbidden, for a Settings tab the caller may not open (AC1)', async () => {
    const { harness } = await setUp();
    const router = TestBed.inject(Router);
    const url = `/orgs/${ORG.id}/settings/members`;

    // `membership:read` is absent: the tab is absent, and its route is as unmatched as an unknown
    // segment — the app's single wildcard renders the 404 pattern with the URL untouched.
    await harness.navigateByUrl(url);

    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(url);
    expect(router.url).not.toBe('/forbidden');
  });

  it('keeps the area stub table for the other settings destinations', async () => {
    const quota = entryOf('quota');
    const { harness } = await setUp(
      new FakeIdentityData(),
      new Set(['organization:read', 'usage:read']),
    );

    await harness.navigateByUrl(urlOf(quota));

    // `quota` is gated by `usage:read` and served by `features/settings/settings.routes.ts`, which
    // no tab task has replaced yet: the placeholder table is still its destination.
    expect(TestBed.inject(Router).url).toBe(`/orgs/${ORG.id}/usage`);
    expect(document.querySelector('app-route-stub')).not.toBeNull();
    expect(document.querySelector('h1')?.textContent?.trim()).toBe(quota.label);
  });
});

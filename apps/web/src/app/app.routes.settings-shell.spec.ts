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
  // The route table loads these lazily; the first navigation otherwise pays their module graph
  // transform inside a test body, which is what grows under host load. Same loaders the table uses.
  beforeAll(async () => {
    await Promise.all([
      import('./features/settings/settings-shell.routes'),
      import('./features/settings/settings.routes'),
      import('./shell/layout/shell.component'),
    ]);
  });

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
    expect(renderedMarker()).toBe('quota-panel');
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

  it('opens Settings on a tab grant alone, without organization:read', async () => {
    const { harness } = await setUp(new FakeIdentityData(), new Set(['usage:read']));

    await harness.navigateByUrl(urlOf(entryOf('settings')));

    expect(TestBed.inject(Router).url).toBe(`/orgs/${ORG.id}/settings/quota-retention`);
    expect(renderedMarker()).toBe('quota-panel');
  });

  it('still refuses Settings with neither organization:read nor any tab grant', async () => {
    const { harness } = await setUp(new FakeIdentityData(), new Set(['project:read']));

    await harness.navigateByUrl(urlOf(entryOf('settings')));

    expect(TestBed.inject(Router).url).toBe('/forbidden');
  });

  it('opens the quota panel from the AI credits shortcut with usage read alone', async () => {
    const quota = entryOf('quota');
    const { harness } = await setUp(new FakeIdentityData(), new Set(['usage:read']));

    await harness.navigateByUrl(urlOf(quota));

    // Usage readers do not need the separate grant that opens the Settings shell.
    expect(TestBed.inject(Router).url).toBe(`/orgs/${ORG.id}/usage`);
    expect(renderedMarker()).toBe('quota-panel');
  });
});

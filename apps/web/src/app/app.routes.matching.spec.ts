// The route table at router level, against the real guards, the real entry routes and the real
// `SessionStore` — only the two seams that talk to the server are faked (`IdentityDataService` and
// `EffectivePermissionsSource`), because the point of these cases is what the client decides from a
// server-declared set, not what the server answers.
//
// What is stubbed, and why: `app.routes.ts` is imported unchanged and walked, replacing only the
// *components* whose own specs cover their templates — the shell host and the four pattern pages,
// which the table loads through `loadComponent`, and the Settings shell's three pages — with
// marker components. Every route, guard, `data.entryId` and lazy boundary in the table under test
// is the production one; `RouteStubPage` and `ProjectOverviewPage` are rendered for real, and the
// Settings shell table is reached through its real `ENTRY_ROUTES` override.
//
// The five behaviours these cases cover:
//   - a route whose permission the server-declared set lacks is not matched → `/forbidden`;
//   - a foreign org id, a foreign project id and a made-up path render the SAME not-found page;
//   - a later-phase entry does not match in the G1 build → not found;
//   - an anonymous session is sent to `/login` with the original URL;
//   - an empty permission set sends every active entry to `/forbidden`;
// plus the two landing rules (first org/project, and the deep link a stub renders).

import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { ACTIVE_PHASE } from './shell/nav/product-phase';
import { NAV_ENTRIES } from './shell/nav/nav-registry';
import {
  BASE_PERMISSIONS,
  entryWithPermission,
  FakeIdentityData,
  FORBIDDEN_MARKER,
  FOREIGN_ORG_ID,
  FOREIGN_PROJECT_ID,
  LoginStub,
  NOT_FOUND_MARKER,
  ORG,
  PROJECT,
  renderedMarker,
  setUp,
  urlOf,
} from './app.routes.test-support';

describe('App routes (09.1.1-T4.1)', () => {
  it('does not match a route whose permission the server-declared set lacks (AC4)', async () => {
    const authoring = entryWithPermission('authoring:read');
    const { harness } = await setUp();
    const router = TestBed.inject(Router);

    // `project:read` is present, `authoring:read` is not: the route exists, the grant does not.
    await harness.navigateByUrl(urlOf(authoring));

    expect(router.url).toBe('/forbidden');
    expect(renderedMarker()).toBe(FORBIDDEN_MARKER);
  });

  it('renders the same not-found page for a foreign org, a foreign project and an unknown path', async () => {
    const { harness } = await setUp();
    const router = TestBed.inject(Router);
    const foreignOrgUrl = `/orgs/${FOREIGN_ORG_ID}/overview`;
    const foreignProjectUrl = `/orgs/${ORG.id}/projects/${FOREIGN_PROJECT_ID}/overview`;
    const unknownUrl = '/no-such-screen';

    // A foreign id must never become a 403: that would confirm the id exists in another tenant.
    // The scope guards refuse the match, so the wildcard answers exactly like it does for a path
    // that exists nowhere.
    await harness.navigateByUrl(foreignOrgUrl);
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(foreignOrgUrl);

    await harness.navigateByUrl(foreignProjectUrl);
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(foreignProjectUrl);

    await harness.navigateByUrl(unknownUrl);
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(router.url).toBe(unknownUrl);
  });

  it('does not match a later-phase entry in the G1 build', async () => {
    const laterPhase = NAV_ENTRIES.find((candidate) => candidate.phase !== ACTIVE_PHASE);
    if (laterPhase === undefined) {
      throw new Error('NAV_ENTRIES has no entry from a later phase to check the phase gate with');
    }
    const { harness } = await setUp();

    await harness.navigateByUrl(urlOf(laterPhase));

    // Hidden, never teased: the entry is as absent as a path that does not exist.
    expect(renderedMarker()).toBe(NOT_FOUND_MARKER);
    expect(TestBed.inject(Router).url).toBe(urlOf(laterPhase));
  });

  it('sends an anonymous session to /login, carrying the attempted URL for a deep link', async () => {
    const identity = new FakeIdentityData();
    identity.signedIn = false;
    const { harness } = await setUp(identity);
    const router = TestBed.inject(Router);

    // The root attempt is the root itself, so it needs no `returnUrl` (landing.guard.ts).
    await harness.navigateByUrl('/', LoginStub);
    expect(router.url.split('?')[0]).toBe('/login');

    // A deep link does: it is the URL the sign-in flow has to come back to.
    const deepLink = `/orgs/${ORG.id}/projects/${PROJECT.id}/overview`;
    await harness.navigateByUrl(deepLink, LoginStub);
    const url = router.url;
    expect(url.split('?')[0]).toBe('/login');
    expect(url).toContain('returnUrl=');
    expect(decodeURIComponent(url)).toContain(deepLink);
    expect(renderedMarker()).toBe('login-stub');
  });

  it('renders the entry stub for a deep link to an allowed entry', async () => {
    // The grants add the authoring permissions the placeholder entries of the authoring group need
    // on top of the base grants: `generation` still mounts the shell stub.
    const allowed = new Set([...BASE_PERMISSIONS, 'authoring:read', 'authoring:generate']);
    const entry = NAV_ENTRIES.find(
      (candidate) =>
        candidate.phase === ACTIVE_PHASE &&
        candidate.id !== 'overview' &&
        // `settings` owns a route table of its own (`ENTRY_ROUTES`), covered by the cases below.
        candidate.id !== 'settings' &&
        allowed.has(candidate.permission) &&
        // Entries that own a real screen or table are asserted by their own specs.
        !['members', 'audit', 'authoring', 'ai-settings', 'pending-changes'].includes(candidate.id),
    );
    if (entry === undefined) {
      throw new Error('NAV_ENTRIES has no allowed, active-phase entry without a real screen');
    }
    const { harness } = await setUp(new FakeIdentityData(), allowed);

    await harness.navigateByUrl(urlOf(entry));

    // The harness's own outlet renders the shell host stub, so `navigateByUrl`'s expected-component
    // form can only be used for a route mounted outside the shell (the `/login` cases above): the
    // page under the shell is asserted on what it rendered.
    expect(document.querySelector('h1')?.textContent?.trim()).toBe(entry.label);
  });

  it('lands on the first org and its first project', async () => {
    const { harness } = await setUp();

    await harness.navigateByUrl('/');

    expect(TestBed.inject(Router).url).toBe(`/orgs/${ORG.id}/projects/${PROJECT.id}/overview`);
    expect(document.querySelector('h1')?.textContent?.trim()).toBe(PROJECT.name);
  });

  it('sends every active entry to /forbidden when the server declares no permission', async () => {
    const { harness } = await setUp(new FakeIdentityData(), new Set<string>());
    const router = TestBed.inject(Router);

    for (const entry of NAV_ENTRIES.filter((candidate) => candidate.phase === ACTIVE_PHASE)) {
      await harness.navigateByUrl(urlOf(entry));
      expect(router.url, `entry '${entry.id}' should have been refused`).toBe('/forbidden');
    }
  });
});

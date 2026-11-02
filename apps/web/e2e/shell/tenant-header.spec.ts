// After sign-in the shell reads `/v1/permissions`, a tenant-scoped route: without `X-Evidgate-Org`
// it answers the pre-tenant route and the shell renders unavailable. The fixtures build answers the
// API in process, so Playwright sees no `/v1` request at all; the fixture backend logs what it
// handled instead (`fixture-request-log.ts`), which is also downstream of the whole interceptor
// chain — the stronger place to read the header.
import { expect, test, type Page } from '@playwright/test';

import type { FixtureCallRecord } from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  LIMITED_GRANTS,
  ORG_ACME,
  ORG_LUMEN,
  overviewPath,
  PROJECT_STOREFRONT,
  withVariant,
} from './support';

/** The recorded log spells header names lowercased, so the org header is read by this key. */
const ORG_HEADER_KEY = 'x-evidgate-org';
const PERMISSIONS_PATH = '/v1/permissions';
const ORGS_LIST_PATH = '/v1/orgs';

/** Every request the fixture backend handled in this page so far. */
function recordedRequests(page: Page): Promise<readonly FixtureCallRecord[]> {
  return page.evaluate(() => window.__evidgateFixtureRequests ?? []);
}

/** The requests of one API path, in the order the backend handled them. */
function requestsTo(
  requests: readonly FixtureCallRecord[],
  path: string,
): readonly FixtureCallRecord[] {
  return requests.filter((request) => new URL(request.url, 'http://127.0.0.1').pathname === path);
}

test.describe('tenant header on the shell bootstrap', () => {
  test('the permission read carries the session organization and the org list carries none', async ({
    page,
  }) => {
    // The default corpus session holds no `project:read`, so the project screen is reached through
    // the trimmed variant, as in `scope-theme.spec.ts`.
    await page.goto(withVariant(overviewPath('en', ORG_ACME, PROJECT_STOREFRONT), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toBeVisible();

    const requests = await recordedRequests(page);
    const permissions = requestsTo(requests, PERMISSIONS_PATH);
    // The shell cannot be authenticated without this read: an empty list means the seam moved.
    expect(permissions.length).toBeGreaterThan(0);
    expect(permissions.map((request) => request.headers[ORG_HEADER_KEY]?.[0] ?? null)).toEqual(
      permissions.map(() => ORG_ACME),
    );

    // `/v1/orgs` answers the caller's own organizations, so it must stay pre-tenant: a header here
    // would scope the list that decides which tenants exist.
    const orgLists = requestsTo(requests, ORGS_LIST_PATH);
    expect(orgLists.length).toBeGreaterThan(0);
    expect(orgLists.map((request) => request.headers[ORG_HEADER_KEY]?.[0] ?? null)).toEqual(
      orgLists.map(() => null),
    );
  });

  test('switching organization sends the next permission read for that organization', async ({
    page,
  }) => {
    // The default corpus session holds no `project:read`, so it never reaches the project screen.
    // The trimmed variant grants it, which is how `scope-theme.spec.ts` reaches the switcher too.
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toBeVisible();
    const before = (await recordedRequests(page)).length;

    const orgSelect = page.locator('app-scope-switcher select').first();
    await expect(orgSelect).toBeEnabled();
    await expect(orgSelect).toHaveValue(ORG_ACME);
    await orgSelect.selectOption(ORG_LUMEN);

    // The scope moved, and the documented destination of that move is the 403 pattern: the landing
    // an org with no attributed project gets needs `project:create`, which this grant set lacks.
    await expect
      .poll(() => new URL(page.url()).pathname, { timeout: 15_000 })
      .toBe('/en/forbidden');
    await expect(page.locator('#main-content h1')).toHaveText('Not authorized');
    await expect(orgSelect).toHaveValue(ORG_LUMEN);

    // The grants are per tenant, so the store reloads them under the new one. Waiting on the record
    // keeps the assertion independent of how many permission reads the switch needs.
    await expect
      .poll(
        async () =>
          requestsTo((await recordedRequests(page)).slice(before), PERMISSIONS_PATH).length,
        { timeout: 15_000 },
      )
      .toBeGreaterThan(0);
    const switched = requestsTo((await recordedRequests(page)).slice(before), PERMISSIONS_PATH);
    expect(switched.map((request) => request.headers[ORG_HEADER_KEY]?.[0] ?? null)).toEqual(
      switched.map(() => ORG_LUMEN),
    );
  });
});

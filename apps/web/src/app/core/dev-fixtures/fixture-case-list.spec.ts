import { HttpRequest } from '@angular/common/http';
import type { CaseListResponse } from '@evidgate/sdk';
import { caseListFixtureBody } from './fixture-case-list';
import { FIXTURE_CATALOG_CASE_ROWS } from './fixture-catalog-case-rows';

describe('catalog case list specimens', () => {
  it('filters by server status and exact framework while retaining the supplied continuation', () => {
    const entry = FIXTURE_CATALOG_CASE_ROWS[0];
    if (!entry) throw new Error('No catalog case fixture');
    const page = caseListFixtureBody(
      entry,
      new HttpRequest('GET', '/v1/projects/p/cases?status=missing&framework=playwright'),
    ) as CaseListResponse;
    expect(page.items.map((row) => row.annotation_id)).toEqual(['case-address-8']);
    expect(page.next_cursor).toBe('catalog-more-opaque');
    const original = entry.document.body as CaseListResponse;
    expect(original.items).toHaveLength(3);
    const noMatch = caseListFixtureBody(
      entry,
      new HttpRequest('GET', '/v1/projects/p/cases?framework=Playwright'),
    ) as CaseListResponse;
    expect(noMatch.items).toEqual([]);
  });
  it('does not turn a forbidden response into an empty success', () => {
    const entry = FIXTURE_CATALOG_CASE_ROWS.find((row) => row.document.status === 403);
    if (!entry) throw new Error('No forbidden specimen');
    expect(
      caseListFixtureBody(entry, new HttpRequest('GET', '/v1/projects/p/cases?status=active')),
    ).toEqual(entry.document.body);
  });
});

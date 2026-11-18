import { HttpRequest } from '@angular/common/http';
import type { CaseListResponse } from '@evidgate/sdk';
import { caseListFixtureBody } from './fixture-case-list';
import { FIXTURE_CATALOG_CASE_ROWS } from './fixture-catalog-case-rows';

describe('catalog case list specimens', () => {
  it('excludes archived cases by default and returns them only for an explicit archived filter', () => {
    const entry = FIXTURE_CATALOG_CASE_ROWS.find((row) => row.document.variant === 'ok');
    if (!entry) throw new Error('No catalog case fixture');
    const page = caseListFixtureBody(
      entry,
      new HttpRequest('GET', '/v1/projects/p/cases'),
    ) as CaseListResponse;
    expect(page.items.map((row) => row.status)).toEqual(['active', 'missing']);
    expect(page.items.map((row) => row.annotation_id)).toEqual([
      '018f0000-0000-7000-8000-0000000c0017',
      '018f0000-0000-7000-8000-0000000c0008',
    ]);
    expect(page.next_cursor).toBe('catalog-more-opaque');
    const archived = caseListFixtureBody(
      entry,
      new HttpRequest('GET', '/v1/projects/p/cases?status=archived'),
    ) as CaseListResponse;
    expect(archived.items.map((row) => row.annotation_id)).toEqual([
      '018f0000-0000-7000-8000-0000000c0002',
    ]);
    expect(archived.items.map((row) => row.status)).toEqual(['archived']);
    expect(archived.next_cursor).toBeNull();
    const noMatch = caseListFixtureBody(
      entry,
      new HttpRequest('GET', '/v1/projects/p/cases?status=archived&framework=playwright'),
    ) as CaseListResponse;
    expect(noMatch.items).toEqual([]);
  });
  it('filters by server status and exact framework while retaining the supplied continuation', () => {
    const entry = FIXTURE_CATALOG_CASE_ROWS[0];
    if (!entry) throw new Error('No catalog case fixture');
    const page = caseListFixtureBody(
      entry,
      new HttpRequest('GET', '/v1/projects/p/cases?status=missing&framework=playwright'),
    ) as CaseListResponse;
    expect(page.items.map((row) => row.annotation_id)).toEqual([
      '018f0000-0000-7000-8000-0000000c0008',
    ]);
    expect(page.next_cursor).toBe('catalog-more-opaque');
    const original = entry.document.body as CaseListResponse;
    expect(original.items).toHaveLength(2);
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

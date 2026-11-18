import { HttpRequest } from '@angular/common/http';
import type { CaseListResponse } from '@evidgate/sdk';
import { caseListFixtureBody } from './fixture-case-list';
import {
  BULK_CASE_ROW_COUNT,
  BULK_CASE_VARIANT,
  findBulkCaseListFixture,
} from './fixture-case-list-bulk';
import { FIXTURE_CATALOG_CASE_ROWS } from './fixture-catalog-case-rows';
import { findFixtureEntry, findFixtureEntryByCursor } from './fixture-catalog';

/** Any uuid shape; the contract's `format: uuid` accepts every version. */
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

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
    const entry = FIXTURE_CATALOG_CASE_ROWS.find(
      (row) => row.document.status === 403 && row.document.operation_id === 'listCases',
    );
    if (!entry) throw new Error('No forbidden specimen');
    expect(
      caseListFixtureBody(entry, new HttpRequest('GET', '/projects/p/cases?status=active')),
    ).toEqual(entry.document.body);
  });

  it('answers the first page cursor with the second page and keeps its rows', () => {
    const page = findFixtureEntryByCursor('listCases', 'catalog-more-opaque');
    if (!page) throw new Error('No second catalog page');
    const body = page.document.body as CaseListResponse;
    expect(body.items.map((row) => row.annotation_id)).toEqual([
      '018f0000-0000-7000-8000-0000000c0021',
    ]);
    expect(body.next_cursor).toBeNull();
    const served = caseListFixtureBody(
      page,
      new HttpRequest('GET', '/projects/p/cases?cursor=catalog-more-opaque&limit=50'),
    ) as CaseListResponse;
    expect(served.items).toEqual(body.items);
  });

  it('registers the owner write answer and its three refusal states', () => {
    expect(findFixtureEntry('updateCase', 'ok')?.document.headers['ETag']).toBe('"4"');
    const variants = FIXTURE_CATALOG_CASE_ROWS.filter(
      (row) => row.document.operation_id === 'updateCase',
    ).map((row) => `${row.document.variant}:${row.document.status}`);
    expect(variants).toEqual([
      'ok:200',
      'forbidden:403',
      'conflict:409',
      'precondition-failed:412',
    ]);
  });

  it('serves one computed page past the virtualisation threshold for the bulk toggle', () => {
    expect(findBulkCaseListFixture('ok')).toBeUndefined();
    const entry = findBulkCaseListFixture(BULK_CASE_VARIANT);
    if (!entry) throw new Error('No bulk catalog page');
    const body = entry.document.body as CaseListResponse;
    expect(body.items).toHaveLength(BULK_CASE_ROW_COUNT);
    expect(body.next_cursor).toBeNull();
    const ids = body.items.map((row) => row.id);
    expect(new Set(ids).size).toBe(BULK_CASE_ROW_COUNT);
    expect(ids.every((id) => UUID.test(id))).toBe(true);
  });
});

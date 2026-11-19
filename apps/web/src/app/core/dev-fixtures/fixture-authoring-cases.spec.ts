import { HttpRequest } from '@angular/common/http';
import type { ManualCaseListResponse } from '@evidgate/sdk';
import { describe, expect, it } from 'vitest';
import { AuthoringCasesFixtureScenario } from './fixture-authoring-cases';
import { isRecord, VALIDATOR } from './fixture-contract.support';

const PATH = '/projects/018f0000-0000-7000-8000-000000000011/authoring/manual-cases';
const request = (query = '') => new HttpRequest('GET', PATH + query);

describe('authoring manual case fixture contract', () => {
  it('validates every served state, UUID and required error member against OpenAPI', () => {
    for (const variant of [
      'ok',
      'loading',
      'empty',
      'error',
      'forbidden',
      'unavailable',
      'bulk',
      'repeated-cursor',
    ]) {
      const entry = new AuthoringCasesFixtureScenario().entry(request(), variant);
      expect(entry, variant).toBeDefined();
      expect(
        VALIDATOR.validateResponse('listManualCases', entry.document.status, entry.document.body)
          .errors,
        variant,
      ).toEqual([]);
    }
  });

  it('applies node, item, lifecycle, priority, coverage and title search on the whole fixture dataset', () => {
    const scenario = new AuthoringCasesFixtureScenario();
    const result = scenario.entry(
      request(
        '?node_id=018f0000-0000-7000-8000-000000000101&item_id=018f0000-0000-7000-8000-000000000201&state=draft&priority=high&covered=true&q=địa',
      ),
      'ok',
    );
    const body = result.document.body as ManualCaseListResponse;
    expect(body.items).toHaveLength(1);
    expect(body.items[0]?.linked_case_ids).toEqual(['018f0000-0000-7000-8000-000000000777']);
    expect(
      (
        scenario.entry(request('?covered=false'), 'ok').document.body as ManualCaseListResponse
      ).items.every((row) => row.linked_live_count === 0),
    ).toBe(true);
  });

  it('serves disjoint continuation pages and a bulk dataset above the virtual threshold', () => {
    const scenario = new AuthoringCasesFixtureScenario();
    const first = scenario.entry(request('?limit=200'), 'bulk').document
      .body as ManualCaseListResponse;
    const second = scenario.entry(request(`?limit=200&cursor=${first.next_cursor}`), 'bulk')
      .document.body as ManualCaseListResponse;
    expect(first.items).toHaveLength(200);
    expect(second.items).toHaveLength(5);
    expect(second.next_cursor).toBeNull();
    expect(
      second.items.some((row) => first.items.some((prior) => prior.case_id === row.case_id)),
    ).toBe(false);
    expect(VALIDATOR.validateResponse('listManualCases', 200, second).errors).toEqual([]);
  });

  it('proves the validator rejects malformed UUIDs and missing error.retryable', () => {
    const scenario = new AuthoringCasesFixtureScenario();
    const body = scenario.entry(request(), 'ok').document.body as ManualCaseListResponse;
    const invalid = { ...body, items: body.items.map((row) => ({ ...row, case_id: 'invalid' })) };
    expect(
      VALIDATOR.validateResponse('listManualCases', 200, invalid).errors.length,
    ).toBeGreaterThan(0);
    const error = structuredClone(scenario.entry(request(), 'unavailable').document.body);
    if (!isRecord(error) || !isRecord(error['error']))
      throw new Error('The fixture must carry an error envelope.');
    delete error['error']['retryable'];
    expect(VALIDATOR.validateResponse('listManualCases', 503, error).errors.length).toBeGreaterThan(
      0,
    );
  });
});

import { describe, expect, it } from 'vitest';
import {
  caseFiltersFromQuery,
  caseFiltersToQuery,
  manualCaseQuery,
  queryTooLong,
} from './authoring-case-filters';

const NODE = '018f0000-0000-7000-8000-000000000101';
const ITEM = '018f0000-0000-7000-8000-000000000201';

describe('authoring case filters', () => {
  it('maps every supported filter and paging parameter to the generated query', () => {
    const raw = {
      node_id: NODE,
      item_id: ITEM,
      state: 'approved',
      priority: 'high',
      covered: 'false',
      q: 'địa chỉ %_',
      cursor: 'opaque',
      limit: '100',
    };
    const filters = caseFiltersFromQuery(raw);
    expect(manualCaseQuery(raw, filters)).toEqual({ ...raw, covered: false, limit: 100 });
    expect(caseFiltersToQuery(filters)).toEqual({
      item_id: ITEM,
      state: 'approved',
      priority: 'high',
      covered: 'false',
      q: 'địa chỉ %_',
      limit: '100',
    });
  });

  it.each(['draft', 'approved', 'retired'])('retains the contract state %s', (state) => {
    expect(caseFiltersFromQuery({ state }).state).toBe(state);
  });

  it.each(['critical', 'high', 'medium', 'low'])('retains the contract priority %s', (priority) => {
    expect(caseFiltersFromQuery({ priority }).priority).toBe(priority);
  });

  it('distinguishes true, false and unfiltered coverage and ignores unsupported parameters', () => {
    expect(manualCaseQuery({ covered: 'true' })).toEqual({ covered: true, limit: 50 });
    expect(manualCaseQuery({ covered: 'false' })).toEqual({ covered: false, limit: 50 });
    expect(manualCaseQuery({ source: 'ai', owner: 'someone', covered: 'invalid' })).toEqual({
      limit: 50,
    });
  });

  it('counts Unicode code points rather than UTF-16 units at the search boundary', () => {
    expect(queryTooLong('😀'.repeat(200))).toBe(false);
    expect(queryTooLong('😀'.repeat(201))).toBe(true);
  });

  it('counts search points after NFC composition, including decomposed boundary values', () => {
    expect(queryTooLong('e\u0301'.repeat(101))).toBe(false);
    expect(queryTooLong('e\u0301'.repeat(200))).toBe(false);
    expect(queryTooLong('e\u0301'.repeat(201))).toBe(true);
    expect(queryTooLong('😀'.repeat(199) + 'e\u0301')).toBe(false);
    expect(queryTooLong('😀'.repeat(200) + 'e\u0301')).toBe(true);
    expect(queryTooLong('x'.repeat(1_000_000))).toBe(true);
  });

  it('sends UUID filters only when their complete shape is valid', () => {
    expect(manualCaseQuery({ node_id: 'node', item_id: ITEM })).toEqual({
      item_id: ITEM,
      limit: 50,
    });
    expect(manualCaseQuery({ node_id: NODE, item_id: 'item' })).toEqual({
      node_id: NODE,
      limit: 50,
    });
  });

  it.each([1, 37, 200])('preserves the contract page-size boundary %i', (limit) => {
    expect(manualCaseQuery({ limit: String(limit) }).limit).toBe(limit);
  });

  it('does not send unknown lifecycle, priority, page-size or blank filters', () => {
    expect(
      manualCaseQuery({ state: 'in-review', priority: 'urgent', limit: '201', q: '' }),
    ).toEqual({ limit: 50 });
  });
});

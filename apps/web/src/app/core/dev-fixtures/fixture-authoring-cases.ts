import type { HttpRequest } from '@angular/common/http';
import type { ErrorEnvelope, ManualCaseListItemDto, ManualCaseListResponse } from '@evidgate/sdk';
import roots from '../../../../../../fixtures/web-api/authoring-tree-roots/input.json';
import refusal from '../../../../../../fixtures/web-api/authoring-tree-error/input.json';
import type { FixtureEntry } from './fixture-catalog';
import { queryParameter } from './fixture-routes';

const ROOT = '018f0000-0000-7000-8000-000000000101';
const ITEM = '018f0000-0000-7000-8000-000000000201';
const CASES: readonly ManualCaseListItemDto[] = [
  {
    case_id: '018f0000-0000-7000-8000-000000000601',
    code: 'SHOP-601',
    checklist_item_id: ITEM,
    node_id: ROOT,
    title:
      'Tuỳ chọn giao hàng và kiểm tra địa chỉ tại các thị trường bị hạn chế với thông tin khách hàng đầy đủ',
    priority: 'high',
    state: 'draft',
    version: 5,
    linked_live_count: 1,
    linked_case_ids: ['018f0000-0000-7000-8000-000000000777'],
  },
  {
    case_id: '018f0000-0000-7000-8000-000000000602',
    code: 'SHOP-602',
    checklist_item_id: '018f0000-0000-7000-8000-000000000202',
    node_id: ROOT,
    title: 'Địa chỉ giao hàng bắt buộc',
    priority: 'critical',
    state: 'approved',
    version: 2,
    linked_live_count: 0,
    linked_case_ids: [],
  },
  {
    case_id: '018f0000-0000-7000-8000-000000000603',
    code: 'SHOP-603',
    checklist_item_id: '018f0000-0000-7000-8000-000000000203',
    node_id: '018f0000-0000-7000-8000-000000000107',
    title: 'Áp dụng mã giảm giá',
    priority: 'medium',
    state: 'retired',
    version: 3,
    linked_live_count: 1,
    linked_case_ids: ['018f0000-0000-7000-8000-000000000778'],
  },
];

export class AuthoringCasesFixtureScenario {
  private read = false;

  entry(request: HttpRequest<unknown>, variant: string): FixtureEntry {
    const query = (key: string) => queryParameter(request.urlWithParams, key);
    if (variant === 'stale' && this.read) return this.error('error');
    this.read = true;
    if (['error', 'forbidden', 'unavailable'].includes(variant)) return this.error(variant);
    const all =
      variant === 'bulk'
        ? Array.from({ length: 205 }, (_, index): ManualCaseListItemDto => {
            const row = CASES[index % CASES.length];
            if (!row) throw new Error('The authoring case corpus must not be empty.');
            return {
              ...row,
              case_id: `018f0000-0000-7000-8000-${String(1000 + index).padStart(12, '0')}`,
              code: `SHOP-${1000 + index}`,
            };
          })
        : CASES;
    const filtered =
      variant === 'empty'
        ? []
        : all.filter(
            (row) =>
              (!query('node_id') || row.node_id === query('node_id')) &&
              (!query('item_id') || row.checklist_item_id === query('item_id')) &&
              (!query('state') || row.state === query('state')) &&
              (!query('priority') || row.priority === query('priority')) &&
              (!query('covered') || row.linked_live_count > 0 === (query('covered') === 'true')) &&
              (!query('q') ||
                row.title.toLocaleLowerCase().includes((query('q') ?? '').toLocaleLowerCase())),
          );
    const cursor = query('cursor');
    const offset = cursor?.startsWith('authoring-cases-')
      ? Number(cursor.slice('authoring-cases-'.length))
      : 0;
    if (cursor && (!Number.isInteger(offset) || offset <= 0)) return this.error('cursor-invalid');
    const limit = Number(query('limit') ?? 50);
    if (!Number.isInteger(limit) || limit < 1 || limit > 200) return this.error('cursor-invalid');
    const size = variant === 'bulk' ? limit : Math.min(limit, 2);
    const items = filtered.slice(offset, offset + size);
    const next = offset + size < filtered.length ? `authoring-cases-${offset + size}` : null;
    const body: ManualCaseListResponse = {
      items,
      next_cursor: variant === 'repeated-cursor' && cursor ? cursor : next,
    };
    return {
      name: `authoring-cases-${variant}`,
      derived: true,
      document: {
        ...roots,
        operation_id: 'listManualCases',
        variant,
        latency_ms: variant === 'loading' ? 1500 : 0,
        body,
      },
    };
  }

  private error(variant: string): FixtureEntry {
    const forbidden = variant === 'forbidden';
    const invalid = variant === 'cursor-invalid';
    const body: ErrorEnvelope = {
      error: {
        ...refusal.body.error,
        code: forbidden ? 'forbidden' : invalid ? 'request_invalid' : 'unavailable',
        message: forbidden
          ? 'The manual-case list is forbidden.'
          : invalid
            ? 'The manual-case query is invalid.'
            : 'The manual-case list is temporarily unavailable.',
        retryable: !forbidden && !invalid,
      },
    };
    return {
      name: `authoring-cases-${variant}`,
      derived: true,
      document: {
        ...refusal,
        operation_id: 'listManualCases',
        variant,
        status: forbidden ? 403 : invalid ? 422 : 503,
        headers: {
          ...refusal.headers,
          ...(variant === 'unavailable' ? { 'Retry-After': '3' } : {}),
        },
        body,
      },
    };
  }
}

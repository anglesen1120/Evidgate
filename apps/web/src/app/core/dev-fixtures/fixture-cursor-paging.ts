import type { HttpRequest } from '@angular/common/http';
import type { AuditListResponse, RunPage } from '@evidgate/sdk';
import audit from '../../../../../../fixtures/web-api/audit-list/input.json';
import runs from '../../../../../../fixtures/web-api/run-list-250/input.json';
import { findFixtureEntry, type FixtureEntry } from './fixture-catalog';
import { queryParameter } from './fixture-routes';

const PREFIX = 'fixture-page.';
type ListBody = RunPage | AuditListResponse;

export class CursorPagingFixtureScenario {
  private readonly failed = new Set<string>();

  entry(
    request: HttpRequest<unknown>,
    operation: string,
    variant: string,
  ): FixtureEntry | undefined {
    if (operation !== 'listRuns' && operation !== 'listAudit') return undefined;
    const scenario = variant === 'paging-33' || variant === 'paging-33-retry';
    const source = scenario
      ? this.scenarioEntry(operation, variant)
      : findFixtureEntry(operation, variant);
    if (!source || source.document.status !== 200) return undefined;
    const cursor = queryParameter(request.urlWithParams, 'cursor');
    if (cursor && !cursor.startsWith(PREFIX)) {
      return scenario ? this.error(operation, 400) : undefined;
    }
    const offset = this.decodeOffset(cursor, operation, variant);
    if (offset === null) return this.error(operation, 400);
    const limitText = queryParameter(request.urlWithParams, 'limit');
    const limit = limitText === null ? (scenario ? 20 : 0) : Number(limitText);
    if (!Number.isSafeInteger(limit) || limit <= 0 || limit > 200) return undefined;
    const body = source.document.body as ListBody;
    const items = this.filteredItems(body.items, request, operation, scenario);
    if (!scenario && !cursor && items.length <= limit) return undefined;
    if (offset > items.length) return this.error(operation, 400);
    const failureKey = `${operation}:${offset}`;
    if (variant === 'paging-33-retry' && offset > 0 && !this.failed.has(failureKey)) {
      this.failed.add(failureKey);
      return this.error(operation, 503);
    }
    const end = offset + limit;
    const next =
      end < items.length
        ? PREFIX + btoa(JSON.stringify({ operation, variant, offset: end }))
        : body.next_cursor;
    return {
      ...source,
      document: {
        ...source.document,
        body: { ...body, items: items.slice(offset, end), next_cursor: next },
      },
    };
  }

  private decodeOffset(cursor: string | null, operation: string, variant: string): number | null {
    if (!cursor) return 0;
    if (cursor.length > 512) return null;
    try {
      const parsed: unknown = JSON.parse(atob(cursor.slice(PREFIX.length)));
      if (
        typeof parsed !== 'object' ||
        parsed === null ||
        !('operation' in parsed) ||
        parsed.operation !== operation ||
        !('variant' in parsed) ||
        parsed.variant !== variant ||
        !('offset' in parsed) ||
        typeof parsed.offset !== 'number' ||
        !Number.isSafeInteger(parsed.offset) ||
        parsed.offset < 1
      )
        return null;
      return parsed.offset;
    } catch {
      return null;
    }
  }

  private filteredItems(
    items: readonly unknown[],
    request: HttpRequest<unknown>,
    operation: string,
    scenario: boolean,
  ): readonly unknown[] {
    const query = new URL(request.urlWithParams, 'https://fixtures.invalid').searchParams;
    return items.filter((row) => {
      if (typeof row !== 'object' || row === null) return false;
      const fields = row as Record<string, unknown>;
      const decision = query.get('decision');
      if (operation === 'listRuns') {
        const value = fields['decision'];
        if (
          decision !== null &&
          (decision === 'pending'
            ? value !== null
            : typeof value !== 'string' || value.toLowerCase() !== decision)
        )
          return false;
        if (!scenario || !query.has('ref')) return true;
        const ref = fields['ref'];
        return (
          typeof ref === 'object' &&
          ref !== null &&
          'name' in ref &&
          typeof ref.name === 'string' &&
          ref.name.includes(query.get('ref') ?? '')
        );
      }
      if (!scenario) return true;
      for (const field of ['actor_type', 'action', 'object_type']) {
        if (query.has(field) && fields[field] !== query.get(field)) return false;
      }
      const created = fields['created_at'];
      if (typeof created !== 'string') return false;
      const timestamp = Date.parse(created);
      const from = query.get('from');
      const to = query.get('to');
      return (
        (from === null || timestamp >= Date.parse(from)) &&
        (to === null || timestamp <= Date.parse(to))
      );
    });
  }

  private scenarioEntry(operation: string, variant: string): FixtureEntry {
    const seed = audit.body.items[0];
    if (!seed) throw new Error('Audit paging requires a seed row');
    const items =
      operation === 'listRuns'
        ? runs.body.items.slice(0, 33)
        : Array.from({ length: 33 }, (_, index) => {
            const row = audit.body.items[index % audit.body.items.length];
            if (!row) throw new Error('Audit paging requires a seed row');
            return {
              ...row,
              id: `018f0000-0000-7000-8000-${String(9000 + index).padStart(12, '0')}`,
              seq: 1000 - index,
              created_at: new Date(Date.parse(seed.created_at) - index * 1000).toISOString(),
            };
          });
    return {
      name: `${operation}-${variant}`,
      document: {
        operation_id: operation,
        variant,
        status: 200,
        latency_ms: 0,
        headers: {},
        body: { items, next_cursor: null },
      },
    };
  }

  private error(operation: string, status: number): FixtureEntry {
    return {
      name: `${operation}-paging-error`,
      document: {
        operation_id: operation,
        variant: 'paging-error',
        status,
        latency_ms: 0,
        headers: {},
        body: {
          error: {
            code: status === 503 ? 'dependency_busy' : 'request_invalid',
            message: 'The requested fixture page could not be read.',
            request_id: 'fixture-paging',
            retryable: status === 503,
            details: [],
          },
        },
      },
    };
  }
}

// Usage-summary answers for the dev fixture backend, in TypeScript rather than the
// `fixtures/web-api` corpus: the summary is a computed read, so its documents are small enough to
// state here, and the corpus stays a record of the wire contract's own examples.
//
// A review picks a state with the usual toggle (`?fixture.variant.getUsageSummary=warn`). The
// variants exist to prove the rules the panel and the badge must never break: a counter with no
// measurement renders "unavailable", never `0`, an empty summary is not an unavailable one, and no
// badge appears without a threshold the server actually recorded.

import type { UsageSummaryDto, UsageSummaryMetricDto } from '@evidgate/sdk';

import type { FixtureEntry } from './fixture-catalog-entries';

/** The states a review can select; `ok` is what the backend answers by default. */
export const USAGE_SUMMARY_VARIANTS = [
  'ok',
  'warn',
  'blocked',
  'empty',
  'unavailable',
  'error',
] as const;

export type UsageSummaryVariant = (typeof USAGE_SUMMARY_VARIANTS)[number];

const PERIOD = '2026-11';
const ERROR_REQUEST_ID = '018f0000-0000-7000-8000-00000000a510';

/** One tenant-scoped counter; a metric with no producer answers null counters and a reason. */
function metric(
  name: UsageSummaryMetricDto['metric'],
  used: number | null,
  limit: number | null,
  changes: Partial<UsageSummaryMetricDto> = {},
): UsageSummaryMetricDto {
  return {
    metric: name,
    scope: 'tenant',
    used,
    limit,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    unavailable_reason: null,
    projects: [],
    ...changes,
  };
}

/** The measured counters a threshold state starts from, well below their limits. */
function measured(): readonly UsageSummaryMetricDto[] {
  return [
    metric('results', 120, 1000),
    metric('ai_tokens', 4000, 50000),
    metric('runner_minutes', 30, 600),
  ];
}

/** One fixture answer: the status it serves, the headers it pins and its body. */
interface UsageSummaryDocument {
  readonly status: number;
  readonly headers: Record<string, string>;
  readonly body: unknown;
}

const DOCUMENTS: Record<UsageSummaryVariant, UsageSummaryDocument> = {
  ok: {
    status: 200,
    headers: {},
    body: { period: PERIOD, project: null, metrics: [...measured()] } satisfies UsageSummaryDto,
  },
  warn: {
    status: 200,
    headers: {},
    body: {
      period: PERIOD,
      project: null,
      metrics: [
        metric('results', 800, 1000, { warned_at: '2026-11-01T09:00:00Z' }),
        ...measured().slice(1),
      ],
    } satisfies UsageSummaryDto,
  },
  blocked: {
    status: 200,
    headers: {},
    body: {
      period: PERIOD,
      project: null,
      metrics: [
        metric('results', 1000, 1000, {
          warned_at: '2026-11-01T09:00:00Z',
          blocked_at: '2026-11-02T09:00:00Z',
          retry_after_seconds: 1_209_600,
        }),
        ...measured().slice(1),
      ],
    } satisfies UsageSummaryDto,
  },
  empty: {
    status: 200,
    headers: {},
    body: { period: PERIOD, project: null, metrics: [] } satisfies UsageSummaryDto,
  },
  unavailable: {
    status: 200,
    headers: {},
    body: {
      period: PERIOD,
      project: null,
      metrics: [
        ...measured(),
        metric('egress_gb', null, null, {
          unavailable_reason: 'storage_usage_daily.egress_bytes has no rows for this period',
        }),
      ],
    } satisfies UsageSummaryDto,
  },
  error: {
    status: 503,
    headers: { 'X-Request-Id': ERROR_REQUEST_ID, 'Retry-After': '5' },
    body: {
      error: {
        code: 'unavailable',
        message: 'the usage service is unavailable for this request',
        request_id: ERROR_REQUEST_ID,
        retryable: true,
        details: [],
      },
    },
  },
};

/**
 * The fixture entry answering `variant`, or `undefined` so the backend answers its own
 * `fixture_not_found` — a typo in the toggle is then visible as a missing fixture, not as a body
 * that happens to parse.
 */
export function findUsageSummaryFixture(variant: string): FixtureEntry | undefined {
  if (!isVariant(variant)) {
    return undefined;
  }
  const { status, headers, body } = DOCUMENTS[variant];
  return {
    name: `usage-summary-${variant}`,
    document: { operation_id: 'getUsageSummary', variant, status, headers, latency_ms: 0, body },
  };
}

function isVariant(value: string): value is UsageSummaryVariant {
  return (USAGE_SUMMARY_VARIANTS as readonly string[]).includes(value);
}

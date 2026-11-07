import type { RetentionPolicy } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog-entries';

export const RETENTION_DEFAULT: RetentionPolicy = { artifact_days: 14, metadata_days: 90 };

const REFUSALS = {
  forbidden: {
    status: 403,
    code: 'forbidden',
    message: 'You may not change retention for this project.',
  },
  'not-found': { status: 404, code: 'not_found', message: 'The project was not found.' },
  conflict: {
    status: 409,
    code: 'project_deleting',
    message: 'This project is being deleted; retention cannot be changed.',
  },
  quota: { status: 429, code: 'quota_exceeded', message: 'The project quota has been exceeded.' },
  unavailable: {
    status: 503,
    code: 'unavailable',
    message: 'Retention is temporarily unavailable.',
  },
} as const;

function policyBody(body: unknown): RetentionPolicy | undefined {
  const value: unknown = typeof body === 'string' ? JSON.parse(body) : body;
  if (
    typeof value !== 'object' ||
    value === null ||
    !('artifact_days' in value) ||
    !('metadata_days' in value)
  )
    return undefined;
  if (typeof value.artifact_days !== 'number' || typeof value.metadata_days !== 'number')
    return undefined;
  return { artifact_days: value.artifact_days, metadata_days: value.metadata_days };
}

export function findRetentionFixture(
  method: string,
  variant: string,
  project: string,
  body: unknown,
  policies: Map<string, RetentionPolicy>,
): FixtureEntry | undefined {
  const operation = method === 'GET' ? 'getRetentionPolicy' : 'setRetentionPolicy';
  const refusal = Object.entries(REFUSALS).find(([key]) => key === variant)?.[1];
  if (refusal) {
    return {
      name: `retention-${operation}-${variant}`,
      document: {
        operation_id: operation,
        variant,
        status: refusal.status,
        headers: {},
        latency_ms: 0,
        body: {
          error: {
            code: refusal.code,
            message: refusal.message,
            request_id: '018f0000-0000-7000-8000-00000000a579',
            retryable: false,
            details: [],
          },
        },
      },
    };
  }
  if (variant !== 'ok') return undefined;
  const answer = method === 'PUT' ? policyBody(body) : (policies.get(project) ?? RETENTION_DEFAULT);
  if (!answer) return undefined;
  if (method === 'PUT') policies.set(project, answer);
  return {
    name: `retention-${operation}-ok`,
    document: {
      operation_id: operation,
      variant,
      status: 200,
      headers: {},
      latency_ms: 0,
      body: answer,
    },
  };
}

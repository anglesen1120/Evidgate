import { ARTIFACT_COPY as c } from './artifact-viewer.copy';

export const artifactStates = [
  'default',
  'loading',
  'empty',
  'filtered',
  'error',
  '403',
  '404',
  '401',
  '429',
  '412',
  'reloaded',
  'trace',
  'restricted',
  'scan',
  'pending_upload',
  'uploaded',
  'rejected',
  'pending_delete',
  'deleted',
  'gated',
  'large',
  'refresh',
  'historical',
] as const;
export type ArtifactState = (typeof artifactStates)[number];
export interface ArtifactViewModel {
  readonly id: string;
  readonly kind: string;
  readonly description: string;
  readonly sensitivity: string;
  readonly initial: ArtifactState;
  readonly digest?: string;
  readonly captured?: string;
}
export const checkout = {
  run: 'run-1842',
  evidence: 'ev-3',
  attempt: 'att-1842-01',
  case: 'case-checkout-17',
  incident: 'inc-91',
  outcome: 'has_failures',
  decision: 'FAIL',
} as const;
export const sandbox = { id: 'ev-s1', run: 'run-sandbox-88', case: 'case-checkout-17' } as const;
export const artifacts: readonly ArtifactViewModel[] = [
  {
    id: 'art-shot-92',
    kind: 'image/png · 248 KiB',
    description: c.shot,
    sensitivity: c.standard,
    initial: 'default',
    digest: 'sha256:ad72…f18b',
    captured: '2026-10-13T09:42:06.718Z',
  },
  {
    id: 'art-trace-91',
    kind: `${c.traceType} · 18.4 MiB`,
    description: c.pending_upload,
    sensitivity: c.traceSensitivity,
    initial: 'pending_upload',
    digest: 'sha256:f83d…621a',
  },
  {
    id: 'art-dom-93',
    kind: c.domType,
    description: c.sensitive,
    sensitivity: c.sensitive,
    initial: 'restricted',
  },
  { id: 'art-log-94', kind: c.logType, description: c.scan, sensitivity: c.scan, initial: 'scan' },
];
const specimenRecord: Partial<Record<ArtifactState, string>> = {
  trace: 'art-trace-91',
  pending_upload: 'art-trace-91',
  uploaded: 'art-trace-91',
  rejected: 'art-trace-91',
  pending_delete: 'art-trace-91',
  deleted: 'art-trace-91',
  large: 'art-trace-91',
  refresh: 'art-trace-91',
  scan: 'art-log-94',
  restricted: 'art-dom-93',
  gated: 'art-dom-93',
};
export function artifactSnapshot(id: string | null, raw: string | null) {
  const requested = artifactStates.find((s) => s === (raw ?? 'default')) ?? '404';
  const specific = specimenRecord[requested];
  const record = artifacts.find((item) => item.id === (id ?? specific ?? 'art-shot-92'));
  if (!record || (id !== null && specific !== undefined && id !== specific))
    return { record: null, state: '404' as const };
  const state = requested === 'default' ? record.initial : requested;
  // An explicit reload must not unlock a restricted or still-pending artifact.
  if (state === 'reloaded' && record.initial !== 'default')
    return { record, state: record.initial };
  return { record, state };
}

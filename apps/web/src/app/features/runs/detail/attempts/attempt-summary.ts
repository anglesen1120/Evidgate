/** Presentation input only; RunStateDto does not expose attempts on the current wire contract. */
export type AttemptStatus = 'passed' | 'failed' | 'skipped' | 'timed_out' | 'error' | 'blocked';

export interface AttemptRow {
  executionKey: string;
  attemptNo: number;
  rawStatus: string;
  normalisedStatus: string;
  durationMs: number | null;
  artifactCount: number | null;
  healingCandidate?: { candidateHash: string; mode: 'shadow' | 'official' } | null;
}

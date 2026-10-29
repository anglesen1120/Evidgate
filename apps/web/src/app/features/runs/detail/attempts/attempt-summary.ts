import type { RunAttemptSummary } from '@evidgate/sdk';

/** Presentation input for one attempt; every member is read verbatim from `RunAttemptSummary`. */
export interface AttemptRow {
  executionKey: string;
  attemptNo: number;
  rawStatus: string;
  normalisedStatus: string;
  durationMs: number | null;
  artifactCount: number | null;
  healingCandidate?: { candidateHash: string; mode: 'shadow' | 'official' } | null;
}

/** Wire row to presentation row. Nothing is derived: statuses and counts are copied as served. */
export function toAttemptRow(wire: RunAttemptSummary): AttemptRow {
  return {
    executionKey: wire.execution_key,
    attemptNo: wire.attempt_no,
    rawStatus: wire.raw_status,
    normalisedStatus: wire.normalized_status,
    durationMs: wire.duration_ms,
    artifactCount: wire.artifact_count,
    healingCandidate:
      wire.healing_candidate === null
        ? null
        : {
            candidateHash: wire.healing_candidate.candidate_hash,
            mode: wire.healing_candidate.mode,
          },
  };
}

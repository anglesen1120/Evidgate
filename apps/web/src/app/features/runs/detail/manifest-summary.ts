import type { RunStateDto } from '@evidgate/sdk';

/** Presentation model of the manifest members `GET /v1/runs/{r}` serves (F44, S143). */
export interface ManifestSummary {
  readonly manifestHash: string | null;
  readonly suiteRevisionId: string | null;
  readonly policyRevisionId: string | null;
  readonly shardCount: number | null;
  readonly producers: readonly number[] | readonly string[] | null;
}

/** The run's manifest members verbatim; `policy_revision_id` is the only nullable one. */
export function manifestFromRun(run: RunStateDto): ManifestSummary {
  return {
    manifestHash: run.manifest_hash,
    suiteRevisionId: run.suite_revision_id,
    policyRevisionId: run.policy_revision_id,
    shardCount: run.shard_count,
    producers: run.declared_producers,
  };
}

export const UNAVAILABLE_MANIFEST: ManifestSummary = {
  manifestHash: null,
  suiteRevisionId: null,
  policyRevisionId: null,
  shardCount: null,
  producers: null,
};

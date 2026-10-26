/** Presentation model, not a wire DTO. getRun currently exposes none of these members. */
export interface ManifestSummary {
  readonly manifestHash: string | null;
  readonly suiteRevisionId: string | null;
  readonly policyRevisionId: string | null;
  readonly shardCount: number | null;
  readonly producers: readonly number[] | readonly string[] | null;
}

export const UNAVAILABLE_MANIFEST: ManifestSummary = {
  manifestHash: null,
  suiteRevisionId: null,
  policyRevisionId: null,
  shardCount: null,
  producers: null,
};

export const MISSING_CODES = [
  'missing_shard',
  'count_mismatch',
  'artifact_missing',
  'build_mismatch',
  'unknown_status',
  'setup_error',
  'stale_epoch',
  'missing_result',
] as const;

export type MissingCode = (typeof MISSING_CODES)[number];

export type DecisionViewModel =
  | { readonly kind: 'pending' }
  | {
      readonly kind: 'evaluated';
      readonly decision: 'PASS' | 'FAIL' | 'INCONCLUSIVE';
      readonly evidenceRevision: number;
      readonly completeness: 'complete' | 'incomplete';
      readonly outcome: 'all_passed' | 'has_failures' | 'no_results';
      readonly missing: readonly string[];
      readonly reasons: readonly string[];
      readonly policyHash: string;
      readonly manifestHash: string;
      readonly evaluatorVersion: string;
      readonly mode: 'live';
    }
  | { readonly kind: 'unavailable'; readonly cause: 'contract' | 'shadow-ignored' | 'error' };

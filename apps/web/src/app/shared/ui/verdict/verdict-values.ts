/** Separate render-only dimensions from 00 §8.5, not API payload DTOs. */
export type Completeness = 'complete' | 'incomplete';
export type Outcome = 'all_passed' | 'has_failures' | 'no_results';
export type DecisionValue = 'PASS' | 'FAIL' | 'INCONCLUSIVE';

import type {
  Completeness,
  DecisionValue,
  Outcome,
} from '../../../shared/ui/verdict/verdict-values';
import type { MappingState } from './mapping-review-states';

export interface MappingRowViewModel {
  readonly id: string;
  readonly name: string;
  readonly state: 'unmapped' | 'conflicted' | 'resolved';
  readonly suggestion: string;
  readonly candidates: readonly { readonly id: string; readonly title: string }[];
}

export interface MappingResolutionViewModel {
  readonly state: MappingState;
  readonly project: string;
  readonly projectKey: string;
  readonly producer: string;
  readonly suite: string;
  readonly report: string;
  readonly run: string;
  readonly evidence: string;
  readonly digest: string;
  readonly completeness: Completeness;
  readonly outcome: Outcome;
  readonly decision: DecisionValue;
  readonly rows: readonly MappingRowViewModel[];
  readonly imports: readonly {
    readonly id: string;
    readonly name: string;
    readonly original: string;
    readonly normalized: string;
    readonly reason: string;
    readonly candidates: string;
  }[];
}

export type MappingPreview = {
  readonly mode: 'bind' | 'dismiss';
  readonly mapping: string;
  readonly candidate: string | null;
};

import type { ReleaseDetail } from '@evidgate/sdk';

/** Region state of the release read; `idle` is before the first request for this run. */
export type ReleaseStatus = 'idle' | 'loading' | 'ready' | 'error' | 'not-found' | 'forbidden';

/** One release component as served. */
export interface ReleaseComponentView {
  readonly componentKey: string;
  readonly digest: string;
  readonly environmentRevisionId: string;
}

/** One `bound_runs` row: a release component bound to one run. */
export interface ReleaseBoundRunView {
  readonly runId: string;
  readonly componentKey: string;
}

/**
 * Presentation model of `GET /v1/releases/{id}` for the run-detail panel: the served members
 * verbatim, the components in wire order, and every binding row. The bindings stay rows, not
 * per-component flags, so the panel decides which run they name — a panel re-bound to another
 * run must not inherit the previous run's marks.
 */
export interface ReleaseDetailViewModel {
  readonly releaseId: string;
  readonly name: string;
  readonly state: 'draft' | 'active';
  readonly version: number;
  /** Every component the release declares, in the wire's `component_key` order. */
  readonly components: readonly ReleaseComponentView[];
  /** Every stored binding, in served order; the panel narrows them to the run it shows. */
  readonly boundRuns: readonly ReleaseBoundRunView[];
}

/** Copy the served release into the panel's model without narrowing or reordering anything. */
export function releaseFromDetail(release: ReleaseDetail): ReleaseDetailViewModel {
  return {
    releaseId: release.release_id,
    name: release.name,
    state: release.state,
    version: release.version,
    components: release.components.map((component) => ({
      componentKey: component.component_key,
      digest: component.digest,
      environmentRevisionId: component.environment_revision_id,
    })),
    boundRuns: release.bound_runs.map((binding) => ({
      runId: binding.run_id,
      componentKey: binding.component_key,
    })),
  };
}

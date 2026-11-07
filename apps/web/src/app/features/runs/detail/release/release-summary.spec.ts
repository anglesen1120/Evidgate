import type { ReleaseDetail } from '@evidgate/sdk';

import { releaseFromDetail } from './release-summary';

const RUN_ID = '018f0000-0000-7000-8000-000000000071';
const OTHER_RUN = '018f0000-0000-7000-8000-000000000072';

function release(overrides: Partial<ReleaseDetail> = {}): ReleaseDetail {
  return {
    release_id: 'rel-1',
    project_id: 'p-1',
    name: 'Web 2026.11',
    state: 'active',
    created_by: 'user:018f0000-0000-7000-8000-000000000001',
    created_at: '2026-11-01T00:00:00Z',
    version: 2,
    components: [
      {
        component_key: 'api',
        digest: `sha256:${'1'.repeat(64)}`,
        environment_revision_id: 'env-api-3',
      },
      {
        component_key: 'web',
        digest: `sha256:${'2'.repeat(64)}`,
        environment_revision_id: 'env-web-3',
      },
      {
        component_key: 'worker',
        digest: `sha256:${'3'.repeat(64)}`,
        environment_revision_id: 'env-worker-3',
      },
    ],
    bound_runs: [
      { run_id: OTHER_RUN, component_key: 'worker', bound_at: '2026-11-01T00:00:01Z' },
      { run_id: RUN_ID, component_key: 'web', bound_at: '2026-11-01T00:00:02Z' },
    ],
    ...overrides,
  };
}

describe('releaseFromDetail', () => {
  it('keeps the served component order and full member values', () => {
    const view = releaseFromDetail(release());
    expect(view.components.map((component) => component.componentKey)).toEqual([
      'api',
      'web',
      'worker',
    ]);
    expect(view.components.map((component) => component.digest)).toEqual([
      `sha256:${'1'.repeat(64)}`,
      `sha256:${'2'.repeat(64)}`,
      `sha256:${'3'.repeat(64)}`,
    ]);
    expect(view.components.map((component) => component.environmentRevisionId)).toEqual([
      'env-api-3',
      'env-web-3',
      'env-worker-3',
    ]);
  });

  it('keeps every binding row as served, for any run, without narrowing or dropping one', () => {
    const view = releaseFromDetail(release());
    expect(view.boundRuns).toEqual([
      { runId: OTHER_RUN, componentKey: 'worker' },
      { runId: RUN_ID, componentKey: 'web' },
    ]);
  });

  it('copies the served release members verbatim', () => {
    const served = release({ state: 'draft', version: 1 });
    const view = releaseFromDetail(served);
    expect(view.releaseId).toBe(served.release_id);
    expect(view.name).toBe(served.name);
    expect(view.state).toBe('draft');
    expect(view.version).toBe(1);
  });

  it('keeps a release that declares no components or bindings empty instead of inventing one', () => {
    const view = releaseFromDetail(release({ components: [], bound_runs: [] }));
    expect(view.components).toEqual([]);
    expect(view.boundRuns).toEqual([]);
  });
});

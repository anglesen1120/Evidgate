import type { ReleaseDetail } from '@evidgate/sdk';
import projectDetail from '../../../../../../fixtures/web-api/project-detail/input.json';
import runDetail from '../../../../../../fixtures/web-api/run-detail/input.json';
import runForbidden from '../../../../../../fixtures/web-api/run-detail-forbidden/input.json';
import runNotFound from '../../../../../../fixtures/web-api/run-detail-not-found/input.json';
import type { FixtureCatalogRow, FixtureDocument } from './fixture-document';

export const RELEASE_BINDINGS = [
  {
    release_id: '018f0000-0000-7000-8000-000000000091',
    release_name: `Storefront release ${'long-name-'.repeat(10)}`,
    component_key: 'web',
  },
  {
    release_id: '018f0000-0000-7000-8000-000000000092',
    release_name: `API release ${'long-name-'.repeat(10)}`,
    component_key: 'api',
  },
] as const;

function releaseDocument(index: 0 | 1): FixtureDocument {
  const binding = RELEASE_BINDINGS[index];
  const body: ReleaseDetail = {
    release_id: binding.release_id,
    project_id: projectDetail.body.id,
    name: binding.release_name,
    state: 'active',
    version: 2,
    created_by: 'user:018f0000-0000-7000-8000-000000000001',
    created_at: '2026-11-01T00:00:00Z',
    components: [
      {
        component_key: binding.component_key,
        digest: `sha256:${(index === 0 ? 'a' : 'b').repeat(64)}`,
        environment_revision_id: '018f0000-0000-7000-8000-000000000093',
      },
    ],
    bound_runs: [
      {
        run_id: runDetail.body.run_id,
        component_key: binding.component_key,
        bound_at: '2026-11-01T00:00:01Z',
      },
    ],
  };
  return { ...runDetail, operation_id: 'getRelease', variant: index === 0 ? 'ok' : 'second', body };
}

export const FIXTURE_CATALOG_RELEASE_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'release-detail', document: releaseDocument(0), derived: true },
  { name: 'release-detail-second', document: releaseDocument(1), derived: true },
  {
    name: 'release-detail-forbidden',
    document: { ...runForbidden, operation_id: 'getRelease', variant: 'forbidden' },
    derived: true,
  },
  {
    name: 'release-detail-not-found',
    document: { ...runNotFound, operation_id: 'getRelease', variant: 'not-found' },
    derived: true,
  },
];

import { getRelease } from '@evidgate/sdk';
import { isApiRequestError } from '../http/api-error';
import { rejectionOf, setupFixtureBackend } from './fixture-backend.test-support';
import { FIXTURE_CATALOG, findFixtureEntry } from './fixture-catalog';
import { RELEASE_BINDINGS } from './fixture-catalog-release-rows';
import { VALIDATOR, isRecord } from './fixture-contract.support';

function boundReleaseIds(body: unknown): string[] {
  if (!isRecord(body)) throw new Error('a served run has no object body');
  const releases: unknown = body['releases'] ?? [];
  if (!Array.isArray(releases)) throw new Error('a served run has no releases array');
  const bindings: readonly unknown[] = releases;
  return bindings.map((binding) => {
    if (!isRecord(binding) || typeof binding['release_id'] !== 'string') {
      throw new Error('a run release binding has no string release_id');
    }
    return binding['release_id'];
  });
}

describe('release fixtures', () => {
  it('pins distinct served release ids and every successful bound-run scenario', () => {
    const releases = FIXTURE_CATALOG.filter(
      ({ document }) =>
        document.operation_id === 'getRelease' && document.status >= 200 && document.status < 300,
    );
    expect(releases.length).toBeGreaterThanOrEqual(2);
    const ids = releases.map(({ document }) => {
      if (!isRecord(document.body) || typeof document.body['release_id'] !== 'string') {
        throw new Error('a successful served release has no string release_id');
      }
      return document.body['release_id'];
    });
    expect(new Set(ids).size, 'two served release documents share one release_id').toBe(ids.length);

    const runs = FIXTURE_CATALOG.filter(
      ({ document }) =>
        document.operation_id === 'getRun' && document.status >= 200 && document.status < 300,
    );
    const refused = runs.filter(({ name }) => name === 'run-detail-release-refused');
    expect(refused).toHaveLength(1);
    const refusedIds = boundReleaseIds(refused[0]?.document.body);
    expect(refusedIds).toEqual(['018f0000-0000-7000-8000-000000000094']);
    expect(ids).not.toContain(refusedIds[0]);
    expect(findFixtureEntry('getRelease', 'forbidden')?.document.status).toBe(403);
    expect(findFixtureEntry('getRelease', 'not-found')?.document.status).toBe(404);

    const boundRuns = runs
      .filter(({ name }) => name !== 'run-detail-release-refused')
      .map(({ document }) => boundReleaseIds(document.body))
      .filter((bindings) => bindings.length > 0);
    expect(boundRuns.length).toBeGreaterThanOrEqual(2);
    for (const releaseId of boundRuns.flat()) expect(ids).toContain(releaseId);
  });

  it('answers each release id with its own components through the SDK', async () => {
    const { gateway } = setupFixtureBackend();
    for (const [index, binding] of RELEASE_BINDINGS.entries()) {
      const detail = await gateway.call((client) =>
        getRelease({ client, path: { id: binding.release_id } }),
      );
      expect(detail.release_id).toBe(binding.release_id);
      expect(detail.components.map((component) => component.component_key)).toEqual([
        binding.component_key,
      ]);
      const document = findFixtureEntry('getRelease', index === 0 ? 'ok' : 'second');
      expect(detail).toEqual(document?.document.body);
      expect(VALIDATOR.validateResponse('getRelease', 200, detail).errors).toEqual([]);
    }
  });

  it.each(['forbidden', 'not-found'] as const)(
    'serves the %s release variant for an unknown id',
    async (variant) => {
      const { gateway, toggles } = setupFixtureBackend();
      toggles.setVariant('getRelease', variant);
      const readable = await gateway.call((client) =>
        getRelease({ client, path: { id: RELEASE_BINDINGS[0].release_id } }),
      );
      expect(readable.release_id).toBe(RELEASE_BINDINGS[0].release_id);
      const cause = await rejectionOf(
        gateway.call((client) =>
          getRelease({ client, path: { id: '018f0000-0000-7000-8000-000000000094' } }),
        ),
      );
      expect(isApiRequestError(cause)).toBe(true);
      if (isApiRequestError(cause)) {
        expect(cause.error.kind).toBe(variant === 'forbidden' ? 'forbidden' : 'not_found');
      }
    },
  );
});

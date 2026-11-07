import { getRelease } from '@evidgate/sdk';
import { isApiRequestError } from '../http/api-error';
import { rejectionOf, setupFixtureBackend } from './fixture-backend.test-support';
import { findFixtureEntry } from './fixture-catalog';
import { RELEASE_BINDINGS } from './fixture-catalog-release-rows';
import { VALIDATOR } from './fixture-contract.support';

describe('release fixtures', () => {
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

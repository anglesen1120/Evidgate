// variant_hash follows the server VariantNormalizer: params come from
// project.metadata.evidgate.params only, the hash is stable across tests, retries and shards
// of one project, and a secret-shaped params set is refused without echoing the value.
import { describe, expect, it } from 'vitest';

import {
  CASE_A,
  EMPTY_VARIANT_HASH,
  fakeTest,
  type FakeProject,
  useIdentityResolveSpec,
} from './identity-resolve.testing.js';
import { createIdentityResolver, resolveIdentity, type WarningPayload } from './identity.js';

describe('variant_hash (server VariantNormalizer semantics)', () => {
  useIdentityResolveSpec();

  it('AC3: hashes the empty params of a project without declared params', () => {
    const identity = resolveIdentity(fakeTest(), {});
    expect(identity.variant_hash).toBe(EMPTY_VARIANT_HASH);
  });

  it('AC3: is identical across tests, retries and shards of one project', () => {
    const project: FakeProject = {
      name: 'chromium',
      metadata: { evidgate: { params: { browser: 'chromium', mode: 'full' } } },
    };
    const resolver = createIdentityResolver({});
    const first = resolver(fakeTest({ id: 't1', project }));
    const retry = resolver(fakeTest({ id: 't1', project }));
    const shard = resolver(fakeTest({ id: 't2', file: 'tests/b.spec.ts', project }));
    expect(first.variant_hash).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(retry.variant_hash).toBe(first.variant_hash);
    expect(shard.variant_hash).toBe(first.variant_hash);
  });

  it('AC3: params come from metadata.evidgate.params only, never from `use`', () => {
    const chromium = resolveIdentity(
      fakeTest({ project: { name: 'chromium', use: { browserName: 'chromium' } } }),
      {},
    );
    const firefox = resolveIdentity(
      fakeTest({ project: { name: 'chromium', use: { browserName: 'firefox' } } }),
      {},
    );
    expect(chromium.variant_hash).toBe(EMPTY_VARIANT_HASH);
    expect(firefox.variant_hash).toBe(EMPTY_VARIANT_HASH);

    const declared = resolveIdentity(
      fakeTest({
        project: { name: 'chromium', metadata: { evidgate: { params: { browser: 'firefox' } } } },
      }),
      {},
    );
    expect(declared.variant_hash).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(declared.variant_hash).not.toBe(EMPTY_VARIANT_HASH);
  });

  it('refuses a secret-shaped params set without echoing the value, once per project', () => {
    const warnings: WarningPayload[] = [];
    const project: FakeProject = {
      name: 'chromium',
      metadata: { evidgate: { params: { api_token: 'super-secret-value' } } },
    };
    const resolver = createIdentityResolver({ warn: (warning) => warnings.push(warning) });
    const first = resolver(fakeTest({ id: 't1', project }));
    const second = resolver(fakeTest({ id: 't2', file: 'tests/b.spec.ts', project }));
    expect(first.variant_hash).toBeUndefined();
    expect(second.variant_hash).toBeUndefined();
    const invalid = warnings.filter((warning) => warning.code === 'invalid_variant_params');
    expect(invalid).toHaveLength(1);
    expect(invalid[0]?.message).not.toContain('super-secret-value');
  });

  it('does not change identity for Unicode titles or paths', () => {
    const project: FakeProject = { name: 'chromium' };
    const nfc = resolveIdentity(
      fakeTest({
        id: 't1',
        title: 'renders café 🧪',
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
        project,
      }),
      {},
    );
    const nfd = resolveIdentity(
      fakeTest({
        id: 't2',
        title: 'renders cafe\u0301 🧪',
        file: 'tests/ünïcode.spec.ts',
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
        project,
      }),
      {},
    );
    expect(nfc.case_id).toBe(CASE_A);
    expect(nfd.case_id).toBe(CASE_A);
    expect(nfd.variant_hash).toBe(nfc.variant_hash);
  });
});

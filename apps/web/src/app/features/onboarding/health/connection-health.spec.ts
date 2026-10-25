import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';

import { deriveConnectionHealth } from './connection-health';

const NOW = new Date('2026-10-25T12:00:00Z');
const PAST = '2026-10-25T11:59:59Z';
const FUTURE = '2026-10-25T12:00:01Z';
const BINDING: CiBindingView = {
  id: 'binding-1',
  project_id: 'project-1',
  provider: 'github_actions',
  repository_id: 123,
  repository_full_name: 'acme/storefront',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: [],
  status: 'active',
  activated_at: PAST,
  created_at: PAST,
  version: 1,
};
const TOKEN: IngestTokenView = {
  id: 'token-1',
  project_id: 'project-1',
  name: 'CI uploader',
  prefix: 'public-prefix',
  status: 'active',
  scopes: [],
  expires_at: null,
  overlap_ends_at: null,
  last_used_at: null,
  created_at: PAST,
};

function derive(input: Partial<Parameters<typeof deriveConnectionHealth>[0]> = {}) {
  return deriveConnectionHealth({
    bindings: [],
    tokens: [],
    bindingsTruncated: false,
    tokensTruncated: false,
    now: NOW,
    ...input,
  });
}

describe('deriveConnectionHealth', () => {
  it('reports an active binding as usable', () => {
    const result = derive({ bindings: [BINDING] });
    expect(result.state).toBe('healthy');
    expect(result.reason).toBe('usable');
    expect(result.credentials).toEqual([
      {
        kind: 'binding',
        id: BINDING.id,
        label: BINDING.repository_full_name,
        status: 'active',
        usable: true,
        detail: 'active',
      },
    ]);
    expect(result.lastTokenUseAt).toBeNull();
  });

  it.each([null, undefined, FUTURE])('accepts an active token with expiry %s', (expires_at) => {
    const token = { ...TOKEN };
    if (expires_at === undefined) delete token.expires_at;
    else token.expires_at = expires_at;
    const result = derive({ tokens: [token] });
    expect(result.state).toBe('healthy');
    expect(result.reason).toBe('usable');
    expect(result.credentials[0]).toMatchObject({
      label: 'CI uploader (public-prefix)',
      usable: true,
      detail: 'active',
    });
  });

  it.each([PAST, NOW.toISOString()])(
    'rejects an active token expiring at or before now: %s',
    (expires_at) => {
      const result = derive({ tokens: [{ ...TOKEN, expires_at }] });
      expect(result.state).toBe('unhealthy');
      expect(result.reason).toBe('all-unusable');
      expect(result.credentials[0]).toMatchObject({
        status: 'active',
        usable: false,
        detail: 'expired',
      });
    },
  );

  it('allows a rotated token strictly within its overlap even when its original expiry is past', () => {
    const result = derive({
      tokens: [{ ...TOKEN, status: 'rotated', expires_at: PAST, overlap_ends_at: FUTURE }],
    });
    expect(result.state).toBe('healthy');
    expect(result.reason).toBe('rotating-only');
    expect(result.credentials[0]).toMatchObject({ usable: true, detail: 'rotating' });
  });

  it.each([PAST, NOW.toISOString(), null, undefined])(
    'rejects a rotated token whose overlap ended or is absent: %s',
    (overlap_ends_at) => {
      const token: IngestTokenView = { ...TOKEN, status: 'rotated' };
      if (overlap_ends_at === undefined) delete token.overlap_ends_at;
      else token.overlap_ends_at = overlap_ends_at;
      const result = derive({ tokens: [token] });
      expect(result.state).toBe('unhealthy');
      expect(result.credentials[0]).toMatchObject({ usable: false, detail: 'rotated-ended' });
    },
  );

  it('prefers a non-rotating usable credential over rotating-only wording', () => {
    const result = derive({
      bindings: [BINDING],
      tokens: [{ ...TOKEN, status: 'rotated', overlap_ends_at: FUTURE }],
    });
    expect(result.reason).toBe('usable');
  });

  it('reports pending verification only when nothing is usable, even if a list is truncated', () => {
    const pending = { ...BINDING, status: 'pending_verification' as const };
    const result = derive({ bindings: [pending], tokensTruncated: true });
    expect(result.state).toBe('pending');
    expect(result.reason).toBe('pending-verification');
    expect(result.credentials[0]).toMatchObject({ usable: false, detail: 'pending' });
    expect(derive({ bindings: [pending], tokens: [TOKEN] }).state).toBe('healthy');
  });

  it('reports revoked and expired-only credentials as unhealthy', () => {
    const result = derive({
      tokens: [
        { ...TOKEN, status: 'revoked' },
        { ...TOKEN, id: 'token-2', status: 'expired', expires_at: FUTURE },
      ],
    });
    expect(result.state).toBe('unhealthy');
    expect(result.reason).toBe('all-unusable');
    expect(result.credentials.map((credential) => credential.detail)).toEqual([
      'revoked',
      'expired',
    ]);
    expect(result.credentials.every((credential) => !credential.usable)).toBe(true);
  });

  it('reports a disabled binding as unusable', () => {
    const result = derive({ bindings: [{ ...BINDING, status: 'disabled' }] });
    expect(result.state).toBe('unhealthy');
    expect(result.credentials[0]).toMatchObject({ usable: false, detail: 'disabled' });
  });

  it('distinguishes no credentials from all unusable credentials', () => {
    expect(derive()).toMatchObject({
      state: 'unhealthy',
      reason: 'no-credentials',
      credentials: [],
      lastTokenUseAt: null,
      lastTokenUseIsLowerBound: false,
    });
  });

  it.each(['bindingsTruncated', 'tokensTruncated'] as const)(
    'reports unknown if %s and no usable or pending credential is listed',
    (flag) => {
      expect(derive({ [flag]: true })).toMatchObject({ state: 'unknown', reason: 'truncated' });
      expect(derive({ [flag]: true, tokens: [{ ...TOKEN, status: 'revoked' }] }).state).toBe(
        'unknown',
      );
      expect(derive({ [flag]: true, tokens: [TOKEN] }).state).toBe('healthy');
    },
  );

  it('takes the latest use across every token status, comparing instants rather than strings', () => {
    const result = derive({
      tokens: [
        { ...TOKEN, id: 'active', last_used_at: '2026-10-25T10:00:00Z' },
        { ...TOKEN, id: 'rotated', status: 'rotated', last_used_at: '2026-10-25T10:10:00Z' },
        { ...TOKEN, id: 'expired', status: 'expired', last_used_at: '2026-10-25T11:00:00Z' },
        { ...TOKEN, id: 'revoked', status: 'revoked', last_used_at: '2026-10-25T06:30:00-05:00' },
      ],
      tokensTruncated: true,
    });
    expect(result.lastTokenUseAt).toBe('2026-10-25T06:30:00-05:00');
    expect(result.lastTokenUseIsLowerBound).toBe(true);
    expect(derive({ bindingsTruncated: true }).lastTokenUseIsLowerBound).toBe(false);
    expect(derive({ tokensTruncated: true }).lastTokenUseIsLowerBound).toBe(true);
  });

  it('does not mutate the input metadata or arrays', () => {
    const bindings = Object.freeze([Object.freeze({ ...BINDING })]);
    const tokens = Object.freeze([Object.freeze({ ...TOKEN })]);
    expect(derive({ bindings, tokens }).credentials).toHaveLength(2);
    expect(bindings[0]).toEqual(BINDING);
    expect(tokens[0]).toEqual(TOKEN);
  });
});

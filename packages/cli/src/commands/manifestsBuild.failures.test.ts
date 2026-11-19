// Every refusal of `evidgate manifests build` that comes from the wire or from a malformed
// plan: the 401 purge, the 403 re-mint hint, the 404/5xx passthrough, the page guards, the
// page/member caps and an empty plan. Each exits 3, prints no stdout, and never prints the
// bearer (the shared runner asserts that last part on every path).
import { beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { resetSecrets } from '../redact.js';

import { jsonResponse } from './cliSpec.testing.js';
import {
  bearer,
  member,
  page,
  report,
  revision,
  stubFetch,
  useBuildSpec,
} from './manifestsBuild.testing.js';

const envelope = (code: string, message: string) => ({
  error: { code, message, request_id: 'req-1', retryable: false },
});

describe('manifests build failures', () => {
  const { setup, run, refusal: refuse } = useBuildSpec();

  beforeEach(() => {
    resetSecrets();
  });

  it('unauthorized_purges_the_cache', async () => {
    const { fixture } = await refuse('token_revoked', () =>
      jsonResponse(401, envelope('token_revoked', 'the token was revoked')),
    );
    await expect(readCachedToken(resolveConfig({ env: fixture.env }))).resolves.toBeUndefined();
  });

  it('forbidden_keeps_the_token_and_names_the_permission', async () => {
    const fixture = await setup();
    const stub = stubFetch(() =>
      jsonResponse(403, envelope('permission_denied', 'the credential lacks the permission')),
    );

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    const stderr = outcome.stderr.join('');
    expect(stderr).toContain('evidgate: error permission_denied: ');
    expect(stderr).toContain('suite:plan_read');
    expect(stderr).toContain('re-mint');
    // A scope refusal says nothing about the token's validity: the cache survives.
    await expect(readCachedToken(resolveConfig({ env: fixture.env }))).resolves.toMatchObject({
      token: bearer,
    });
  });

  it('not_found_passes_the_server_body_through', async () => {
    const { outcome } = await refuse('not_found', () =>
      jsonResponse(404, envelope('not_found', 'no such suite revision')),
    );
    expect(outcome.stderr.join('')).toContain('no such suite revision');
    expect(outcome.stderr.join('')).toContain('request_id: req-1');
  });

  it('server_error_is_replayed_then_reported', async () => {
    const fixture = await setup();
    const retryable = {
      error: {
        code: 'server_error',
        message: `unavailable for ${bearer}`,
        request_id: 'req-1',
        retryable: true,
      },
    };
    const stub = stubFetch(() => jsonResponse(503, retryable));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    // A 503 is retryable, so the one GET ran its three attempts before it was reported.
    expect(stub.calls).toHaveLength(3);
    const stderr = outcome.stderr.join('');
    expect(stderr).toContain('evidgate: error server_error: ');
    expect(stderr).toContain('[REDACTED]');
  });

  it('transport_failure_reports_transport', async () => {
    const fixture = await setup();
    const failing: typeof fetch = () => Promise.reject(new TypeError('fetch failed'));

    const outcome = await run(fixture, failing);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error transport: ');
  });

  it.each([
    ['no members member', { suite_revision_id: revision, next_cursor: null }],
    ['non-list members', { suite_revision_id: revision, members: {}, next_cursor: null }],
    ['foreign revision', { ...page(), suite_revision_id: member(9).case_id }],
    [
      'uppercase case id',
      {
        ...page(),
        // A case id with letters in it: uppercasing an all-digit id is a no-op.
        members: [{ ...member(), case_id: 'aaaaaaa1-aaaa-4aaa-8aaa-aaaaaaaaaaa1'.toUpperCase() }],
      },
    ],
    [
      'nil case id',
      { ...page(), members: [{ ...member(), case_id: '00000000-0000-0000-0000-000000000000' }] },
    ],
    ['bad variant hash', { ...page(), members: [{ ...member(), variant_hash: 'sha256:ABC' }] }],
    [
      'uppercase variant hash',
      { ...page(), members: [{ ...member(), variant_hash: `sha256:${'A'.repeat(64)}` }] },
    ],
    [
      'bad case revision',
      { ...page(), members: [{ ...member(), case_revision_id: 'not-a-uuid' }] },
    ],
    ['empty cursor', { ...page(), next_cursor: '' }],
  ])('malformed page (%s) is plan_invalid', async (_label, body) => {
    await refuse('manifest_plan_invalid', () => jsonResponse(200, body));
  });

  it('repeated_cursor_is_plan_invalid', async () => {
    const fixture = await setup({ text: report([member(1), member(2)]) });
    const stub = stubFetch((_call, index) =>
      jsonResponse(200, page(index === 0 ? [member(1)] : [member(2)], 'same-cursor')),
    );

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_plan_invalid: ');
    expect(stub.calls).toHaveLength(2);
  });

  it('a_plan_over_twenty_pages_is_too_large', async () => {
    const members = Array.from({ length: 21 }, (_value, index) => member(index + 1));
    const fixture = await setup({ text: report(members) });
    const stub = stubFetch((_call, index) =>
      jsonResponse(200, page([members[index]!], `cursor-${index + 1}`)),
    );

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_input_too_large: ');
    // The twentieth page still named a next cursor, so a twenty-first page would exist.
    expect(stub.calls).toHaveLength(20);
  });

  it('more_than_ten_thousand_members_is_too_large', async () => {
    const extra = Array.from({ length: 10_001 }, (_value, index) => member(index + 1));
    const fixture = await setup({ text: report([member(1)]) });
    const stub = stubFetch((_call, index) =>
      jsonResponse(
        200,
        page(index === 0 ? extra.slice(0, 5000) : extra.slice(5000), index === 0 ? 'next' : null),
      ),
    );

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_input_too_large: ');
  });

  it('empty_plan_is_plan_invalid', async () => {
    await refuse('manifest_plan_invalid', () => jsonResponse(200, page([], null)));
  });

  it('duplicate_plan_member_is_plan_invalid', async () => {
    const fixture = await setup({ text: report([member(1)]) });
    const stub = stubFetch(() => jsonResponse(200, page([member(1), member(1)], null)));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_plan_invalid: ');
  });
});

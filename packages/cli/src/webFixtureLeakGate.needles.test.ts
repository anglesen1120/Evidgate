// Proofs of apps/web/scripts/check-no-fixture-code.mjs on which corpus strings are needles: a
// permission row's `resource`/`action` words and the enumerated `provider`/`status` values are
// exempt, and every other string of needle length stays a leak.
import { afterEach, describe, expect, it } from 'vitest';

import { cleanupWorkspaces, workspace } from './webFixtureLeakGate.testing.js';

afterEach(cleanupWorkspaces);

describe('no-fixture-code gate', () => {
  it('passes_a_permission_row_whose_resource_and_action_production_carries', async () => {
    const ws = await workspace();
    // A catalogue row as `permissions-list*/input.json` stores it: `organization` (12 characters)
    // is long enough to be a corpus needle, and the navigation registry's own literal
    // `organization:read` contains it, so only skipping `resource`/`action` lets this build pass.
    // The row's `description` is fixture data and stays a needle.
    const description = 'Read the organizations the caller is a member of.';
    await ws.fixture('permissions-list-limited', {
      operation_id: 'listPermissions',
      variant: 'limited',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: {
        items: [
          { code: 'organization:read', resource: 'organization', action: 'read', description },
        ],
      },
    });
    await ws.output('main-ABC123.js', 'const nav=[{permission:"organization:read"}];');

    expect(ws.run().status).toBe(0);

    await ws.output('main-ABC123.js', `const nav=[${JSON.stringify(description)}];`);
    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain(description);
  });

  it('still_scans_resource_and_action_outside_a_permission_row', async () => {
    const ws = await workspace();
    // `action`/`resource` are common field names; the skip applies only where `code` is exactly
    // `<resource>:<action>`. Here they are ordinary fixture data and must stay needles.
    const action = 'archive-the-staging-suite';
    await ws.fixture('audit-events', {
      operation_id: 'listAuditEvents',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: { items: [{ code: 'evt-1', resource: 'suite', action }] },
    });
    await ws.output('main-ABC123.js', `const a=${JSON.stringify(action)};`);

    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain(action);
  });

  it('skips_only_the_enumerated_words_of_the_provider_and_status_members', async () => {
    const ws = await workspace();
    // `provider`/`status` are no longer skipped as whole members. `github_actions`
    // (14 characters) and `pending_verification` (20) are the words the contract enumerates and
    // the step-2 page renders, so a bundle carrying them must still pass. The document carries no
    // other string of needle length, so the reported count proves the exemption did the work:
    // without the allowlist these two words are needles and the bundle holds both.
    const enumerated = { provider: 'github_actions', status: 'pending_verification' };
    await ws.fixture('ci-bindings-list', {
      operation_id: 'listCiBindings',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: {
        items: [{ id: 'cib-1', provider: enumerated.provider, status: enumerated.status }],
      },
    });
    await ws.output(
      'main-ABC123.js',
      `const words=[${JSON.stringify(enumerated.provider)},${JSON.stringify(enumerated.status)}];`,
    );

    const clean = ws.run();
    expect(clean.status).toBe(0);
    expect(clean.stdout).toContain('0 fixture string(s)');

    // Any other word under `status` is fixture data and stays a needle: the exemption is the
    // enumerated value, not the member name.
    const unexpected = 'awaiting-manual-review';
    await ws.fixture('ci-bindings-list', {
      operation_id: 'listCiBindings',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: { items: [{ id: 'cib-1', status: unexpected }] },
    });
    await ws.output('main-ABC123.js', `const s=${JSON.stringify(unexpected)};`);

    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain(unexpected);

    // The exemption is also scoped to those two member names: an enumerated word carried by any
    // other member (`name` here) is ordinary data, so it stays a needle.
    await ws.fixture('ci-bindings-list', {
      operation_id: 'listCiBindings',
      variant: 'default',
      status: 200,
      headers: { 'content-type': 'application/json' },
      latency_ms: 0,
      body: { items: [{ id: 'cib-1', name: enumerated.status }] },
    });
    await ws.output('main-ABC123.js', `const s=${JSON.stringify(enumerated.status)};`);

    const renamed = ws.run();
    expect(renamed.status).toBe(1);
    expect(renamed.stderr).toContain(enumerated.status);
  });
});

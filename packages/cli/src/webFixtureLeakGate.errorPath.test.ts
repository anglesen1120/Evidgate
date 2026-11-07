// Proofs of apps/web/scripts/check-no-fixture-code.mjs on the error envelope's `details[].path`:
// a value naming contract properties is vocabulary, anything else there (or a contract name in
// another member) stays a needle, and an unusable contract fails the gate closed.
import { afterEach, describe, expect, it } from 'vitest';

import { cleanupWorkspaces, type GateWorkspace, workspace } from './webFixtureLeakGate.testing.js';

afterEach(cleanupWorkspaces);

const CONTRACT = `components:
  schemas:
    RetentionPolicy:
      properties:
        artifact_days: { type: integer }
        metadata_days: { type: integer }
    RetentionLimits:
      properties:
        min_artifact_days: { type: integer }
    Holder:
      properties:
        retention_limits: { $ref: '#/components/schemas/RetentionLimits' }
        components:
          type: array
          items:
            properties:
              display_name: { type: string }
`;

function refusal(details: readonly object[], extra: object = {}): unknown {
  return {
    operation_id: 'setRetentionPolicy',
    variant: 'out_of_range',
    status: 422,
    headers: {},
    latency_ms: 0,
    body: {
      error: {
        code: 'retention_out_of_range',
        message: 'window is outside the range',
        request_id: 'req-1',
        retryable: false,
        details,
      },
      ...extra,
    },
  };
}

async function gate(ws: GateWorkspace, document: unknown, bundle: string) {
  await ws.contract(CONTRACT);
  await ws.fixture('retention-put-out-of-range', document);
  await ws.output('main-ABC123.js', bundle);
  return ws.run();
}

describe('no-fixture-code gate: error-envelope path', () => {
  it('passes_a_contract_property_name_under_the_envelope_path', async () => {
    const ws = await workspace();
    const result = await gate(
      ws,
      refusal([{ path: 'artifact_days', reason: 'r' }]),
      'const k={a:"artifact_days"};',
    );
    expect(result.status).toBe(0);
  });

  it('passes_dotted_indexed_and_pointer_paths_of_contract_names', async () => {
    const ws = await workspace();
    const paths = [
      'retention_limits.min_artifact_days',
      'components[0].display_name',
      'components.0.display_name',
      '/components/3/display_name',
    ];
    const result = await gate(
      ws,
      refusal(paths.map((path) => ({ path, reason: 'r' }))),
      `const k=${JSON.stringify(paths)};`,
    );
    expect(result.status).toBe(0);
  });

  it.each([
    ['an invented string', 'invented_member_name'],
    ['a dotted path with an unknown segment', 'retention_limits.invented_member_name'],
    ['an index in the leading position', '0.artifact_days'],
    ['an empty segment', 'retention_limits..min_artifact_days'],
    ['a trailing dot', 'artifact_days.'],
  ])('keeps_%s_under_the_envelope_path_as_a_needle', async (_label, path) => {
    const ws = await workspace();
    const result = await gate(
      ws,
      refusal([{ path, reason: 'r' }]),
      `const k=${JSON.stringify(path)};`,
    );
    expect(result.status).toBe(1);
    expect(result.stderr).toContain(path);
  });

  it('keeps_a_contract_name_in_a_member_other_than_the_envelope_path', async () => {
    const ws = await workspace();
    for (const document of [
      refusal([{ path: 'artifact_days', reason: 'artifact_days', note: 'x' }]),
      refusal([{ reason: 'r' }], { path: 'artifact_days' }),
      refusal([{ reason: 'r', path_info: 'artifact_days' }]),
    ]) {
      const result = await gate(ws, document, 'const k="artifact_days";');
      expect(result.status).toBe(1);
      expect(result.stderr).toContain('artifact_days');
    }
  });

  it('keeps_a_path_outside_error_details_as_a_needle', async () => {
    const ws = await workspace();
    const document = {
      operation_id: 'listThings',
      variant: 'ok',
      status: 200,
      headers: {},
      latency_ms: 0,
      body: { items: [{ path: 'artifact_days' }], error: { path: 'artifact_days' } },
    };
    const result = await gate(ws, document, 'const k="artifact_days";');
    expect(result.status).toBe(1);
  });

  it.each([
    ['missing', null],
    ['unparsable', 'components: [unclosed\n  - : :'],
    ['empty', ''],
    ['without schema properties', 'components:\n  schemas: {}\n'],
  ])('fails_closed_when_the_contract_is_%s', async (_label, text) => {
    const ws = await workspace();
    await ws.contract(text);
    await ws.fixture(
      'retention-put-out-of-range',
      refusal([{ path: 'artifact_days', reason: 'r' }]),
    );
    await ws.output('main-ABC123.js', 'const k=1;');
    const result = ws.run();
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('fixture code:');
  });

  it('still_fails_on_a_real_fixture_leak_beside_an_exempt_path', async () => {
    const ws = await workspace();
    const result = await gate(
      ws,
      refusal([{ path: 'artifact_days', reason: 'a_fixture_only_reason' }]),
      'const k="a_fixture_only_reason";',
    );
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('a_fixture_only_reason');
  });

  it.each([
    [
      'a sibling envelope below another member',
      { body: { items: [{ error: { details: [{ path: 'artifact_days' }] } }] } },
    ],
    ['a root-level envelope', { error: { details: [{ path: 'artifact_days' }] } }],
    [
      'a doubled error member',
      { body: { error: { error: { details: [{ path: 'artifact_days' }] } } } },
    ],
    [
      'an object keyed like the array marker',
      { body: { error: { details: { '[]': { path: 'artifact_days' } } } } },
    ],
    ['details that is an object', { body: { error: { details: { path: 'artifact_days' } } } }],
  ])('keeps_a_contract_name_in_%s_as_a_needle', async (_label, document) => {
    const ws = await workspace();
    const result = await gate(
      ws,
      { operation_id: 'x', variant: 'ok', status: 422, headers: {}, latency_ms: 0, ...document },
      'const k="artifact_days";',
    );
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('artifact_days');
  });

  it('does_not_take_names_from_sample_data_or_a_properties_list', async () => {
    const ws = await workspace();
    await ws.contract(`components:
  schemas:
    Real:
      properties:
        real_member_name: { type: string, example: { properties: { sample_only_name: 1 } } }
      default:
        properties: { default_only_name: 1 }
    Listed:
      properties: [listed_only_name, { properties: { nested_only_name: 1 } }]
`);
    await ws.fixture(
      'probe',
      refusal([
        { path: 'real_member_name', reason: 'r' },
        { path: 'sample_only_name', reason: 'r' },
        { path: 'default_only_name', reason: 'r' },
        { path: '0', reason: 'r' },
        { path: 'nested_only_name', reason: 'r' },
      ]),
    );
    await ws.output('main-ABC123.js', 'const k="sample_only_name";');
    const result = ws.run();
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('sample_only_name');
    await ws.output('main-ABC123.js', 'const k="real_member_name";');
    expect(ws.run().status).toBe(0);
  });

  // The corpus case that had to leave fixtures/web-api (content of
  // fixtures/web-api/retention-put-out-of-range/input.json at 38ea862f^) against the real contract.
  it('passes_the_retention_out_of_range_case_against_the_repository_contract', async () => {
    const ws = await workspace();
    await ws.fixture('retention-put-out-of-range', {
      operation_id: 'setRetentionPolicy',
      variant: 'out_of_range',
      status: 422,
      headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000b10e' },
      latency_ms: 0,
      body: {
        error: {
          code: 'retention_out_of_range',
          message: 'the retention window is outside the configured range',
          request_id: '018f0000-0000-7000-8000-00000000b10e',
          retryable: false,
          details: [
            { path: 'artifact_days', reason: 'below_min_artifact_days' },
            { path: 'retention_limits.min_artifact_days', reason: '1' },
          ],
        },
      },
    });
    await ws.output(
      'main-ABC123.js',
      'const k=["artifact_days","min_artifact_days","retention_limits"];',
    );
    expect(ws.run().status).toBe(0);
    await ws.output('leak.js', 'const r="below_min_artifact_days";');
    const leaked = ws.run();
    expect(leaked.status).toBe(1);
    expect(leaked.stderr).toContain('below_min_artifact_days');
  });
});

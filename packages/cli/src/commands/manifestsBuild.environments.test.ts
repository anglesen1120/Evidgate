import { access, readFile } from 'node:fs/promises';
import { join } from 'node:path';

import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CliError } from '../errors.js';
import { buildManifest } from '../manifestEmit.js';
import { loadPackagedContractSchemas } from '../packagedSchemas.js';
import { resetSecrets } from '../redact.js';

import {
  member,
  page,
  report,
  revision,
  stubFetch,
  useBuildSpec,
} from './manifestsBuild.testing.js';

const defaultBytes =
  '{"schema_version":1,"suite_revision_id":"11111111-1111-4111-8111-111111111111",' +
  '"planned_executions":[{"case_revision_id":"33333333-3333-4333-8333-000000000001","variant":{"params":{}},"target_env":"default"}],' +
  '"matrix":{"case_revision_ids":["33333333-3333-4333-8333-000000000001"],"variants":[{"params":{}}],"target_envs":["default"]},' +
  '"shard_plan":{"shards":[{"shard_id":"shard-1","from_index":0,"to_index":1}]},"env_needs":{"target_envs":[{"target_env":"default"}]}}\n';
const environmentRevision = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';

interface EmittedManifest {
  planned_executions: {
    case_revision_id: string;
    variant: { params: object };
    target_env: string;
  }[];
  matrix: { case_revision_ids: string[]; variants: { params: object }[]; target_envs: string[] };
  shard_plan: { shards: { shard_id: string; from_index: number; to_index: number }[] };
  env_needs: { target_envs: { target_env: string }[] };
  environment_revision_id?: string;
}

const pilotSingle = {
  schema_version: 1,
  suite_revision_id: '11111111-1111-4111-8111-111111111111',
  environment_revision_id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
  planned_executions: [
    {
      case_revision_id: '33333333-3333-4333-8333-000000000001',
      variant: { params: {} },
      target_env: 'e',
    },
  ],
  matrix: {
    case_revision_ids: ['33333333-3333-4333-8333-000000000001'],
    variants: [{ params: {} }],
    target_envs: ['e'],
  },
  shard_plan: { shards: [{ shard_id: 'shard-1', from_index: 0, to_index: 1 }] },
  env_needs: { target_envs: [{ target_env: 'e' }] },
};
const pilotMultiple = {
  schema_version: 1,
  suite_revision_id: '11111111-1111-4111-8111-111111111111',
  environment_revision_id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
  planned_executions: [
    {
      case_revision_id: '33333333-3333-4333-8333-000000000001',
      variant: { params: {} },
      target_env: 'e',
    },
    {
      case_revision_id: '33333333-3333-4333-8333-000000000001',
      variant: { params: {} },
      target_env: 'e-2',
    },
  ],
  matrix: {
    case_revision_ids: ['33333333-3333-4333-8333-000000000001'],
    variants: [{ params: {} }],
    target_envs: ['e', 'e-2'],
  },
  shard_plan: {
    shards: [
      { shard_id: 'shard-1', from_index: 0, to_index: 1 },
      { shard_id: 'shard-2', from_index: 1, to_index: 2 },
    ],
  },
  env_needs: { target_envs: [{ target_env: 'e' }, { target_env: 'e-2' }] },
};

function assertPartition(document: EmittedManifest): void {
  const covered = document.shard_plan.shards.flatMap((shard, index) => {
    expect(shard.shard_id).toBe(`shard-${index + 1}`);
    expect(shard.to_index).toBeGreaterThan(shard.from_index);
    return Array.from(
      { length: shard.to_index - shard.from_index },
      (_, offset) => shard.from_index + offset,
    );
  });
  expect(covered).toEqual(document.planned_executions.map((_, index) => index));
}

describe('manifests build environment and shard emission', () => {
  const { setup, run } = useBuildSpec();
  beforeEach(() => resetSecrets());

  async function buildTwiceAndUpload(
    flags: string[],
    count = 1,
    params: Record<string, unknown> = {},
  ) {
    const members = Array.from({ length: count }, (_, index) => member(index + 1, params));
    const fixture = await setup({ text: report(members, params) });
    const plan = stubFetch(() => Response.json(page(members)));
    const args = [...fixture.args, ...flags];
    const outcome = await run(fixture, plan.fetchImpl, args);
    expect(outcome.code).toBe(0);
    expect(outcome.stderr.join('')).toBe('');
    const text = await readFile(fixture.out, 'utf8');
    expect(loadPackagedContractSchemas().validate('manifest.v1', text).errors).toEqual([]);

    const second = join(fixture.root, 'second.json');
    const replay = [...args];
    replay[replay.indexOf('--out') + 1] = second;
    expect((await run(fixture, plan.fetchImpl, replay)).code).toBe(0);
    expect(await readFile(second, 'utf8')).toBe(text);

    const upload = stubFetch((call) => {
      expect(call.method).toBe('POST');
      expect(call.body.toString('utf8')).toBe(text);
      return Response.json(
        {
          manifest_id: '99999999-9999-4999-8999-999999999999',
          manifest_hash: `sha256:${'a'.repeat(64)}`,
        },
        { status: 201 },
      );
    });
    expect(
      (await run(fixture, upload.fetchImpl, ['manifests', 'upload', '--file', fixture.out])).code,
    ).toBe(0);
    expect(upload.calls).toHaveLength(1);
    return { text, document: JSON.parse(text) as EmittedManifest, outcome };
  }

  it('defaults_are_byte_identical_to_the_original_builder_output', async () => {
    const { text } = await buildTwiceAndUpload([]);
    expect(text).toBe(defaultBytes);
    expect((await buildTwiceAndUpload(['--shards', '1'])).text).toBe(defaultBytes);
  });

  it('multi_env_preserves_member_order_then_flag_order_and_matrix_axes', async () => {
    const { document, outcome } = await buildTwiceAndUpload(
      ['--target-env', 'z-prod', '--target-env', 'a_stage'],
      3,
      { browser: 'chromium' },
    );
    expect(
      document.planned_executions.map((entry) => [entry.case_revision_id, entry.target_env]),
    ).toEqual([
      ['33333333-3333-4333-8333-000000000001', 'z-prod'],
      ['33333333-3333-4333-8333-000000000001', 'a_stage'],
      ['33333333-3333-4333-8333-000000000002', 'z-prod'],
      ['33333333-3333-4333-8333-000000000002', 'a_stage'],
      ['33333333-3333-4333-8333-000000000003', 'z-prod'],
      ['33333333-3333-4333-8333-000000000003', 'a_stage'],
    ]);
    expect(document.matrix).toEqual({
      case_revision_ids: [
        '33333333-3333-4333-8333-000000000001',
        '33333333-3333-4333-8333-000000000002',
        '33333333-3333-4333-8333-000000000003',
      ],
      variants: [{ params: { browser: 'chromium' } }],
      target_envs: ['z-prod', 'a_stage'],
    });
    expect(document.env_needs.target_envs).toEqual([
      { target_env: 'z-prod' },
      { target_env: 'a_stage' },
    ]);
    expect(document.shard_plan.shards).toEqual([
      { shard_id: 'shard-1', from_index: 0, to_index: 6 },
    ]);
    expect(outcome.stdout.join('')).toContain('executions=6 ');
    assertPartition(document);
  });

  it('environment_revision_is_lowercased_and_inserted_in_schema_key_order', async () => {
    const { text, document } = await buildTwiceAndUpload([
      '--environment-revision-id',
      environmentRevision.toUpperCase(),
    ]);
    expect(document.environment_revision_id).toBe(environmentRevision);
    expect(Object.keys(JSON.parse(text) as Record<string, unknown>)).toEqual([
      'schema_version',
      'suite_revision_id',
      'environment_revision_id',
      'planned_executions',
      'matrix',
      'shard_plan',
      'env_needs',
    ]);
  });

  it('numeric_shards_partition_uneven_execution_counts_with_larger_shards_first', async () => {
    const { document } = await buildTwiceAndUpload(
      ['--target-env', 'e', '--target-env', 'e-2', '--shards', '4'],
      3,
    );
    expect(document.shard_plan.shards).toEqual([
      { shard_id: 'shard-1', from_index: 0, to_index: 2 },
      { shard_id: 'shard-2', from_index: 2, to_index: 4 },
      { shard_id: 'shard-3', from_index: 4, to_index: 5 },
      { shard_id: 'shard-4', from_index: 5, to_index: 6 },
    ]);
    assertPartition(document);
  });

  it('per_execution_shards_match_the_numeric_execution_count_byte_for_byte', async () => {
    const environments = ['--target-env', 'e', '--target-env', 'e-2'];
    const perExecution = await buildTwiceAndUpload(
      [...environments, '--shards', 'per-execution'],
      3,
    );
    const numeric = await buildTwiceAndUpload([...environments, '--shards', '6'], 3);
    expect(perExecution.text).toBe(numeric.text);
    expect(perExecution.document.shard_plan.shards).toHaveLength(6);
    assertPartition(perExecution.document);
  });

  it.each([
    { flags: ['--target-env', 'e', '--shards', '1'], expected: pilotSingle },
    {
      flags: ['--target-env', 'e', '--target-env', 'e-2', '--shards', 'per-execution'],
      expected: pilotMultiple,
    },
  ])('pilot_dogfood_matches_the_old_builder_fixture: $flags', async ({ flags, expected }) => {
    const { document } = await buildTwiceAndUpload([
      ...flags,
      '--environment-revision-id',
      environmentRevision,
    ]);
    expect(document).toEqual(expected);
    assertPartition(document);
  });

  it('schema_boundaries_accept_32_environments_and_a_64_character_name', async () => {
    const envs = ['e'.repeat(64), ...Array.from({ length: 31 }, (_, i) => `e-${i}`)];
    const { document } = await buildTwiceAndUpload(envs.flatMap((env) => ['--target-env', env]));
    expect(document.matrix.target_envs).toEqual(envs);
    expect(document.planned_executions).toHaveLength(32);
    assertPartition(document);
  });

  it('schema_boundary_accepts_256_shards', async () => {
    const { document } = await buildTwiceAndUpload(['--shards', '256'], 256);
    expect(document.shard_plan.shards).toHaveLength(256);
    assertPartition(document);
  });

  it('a_partial_matrix_stays_refused_when_environments_multiply_the_executions', async () => {
    // Two cases with one variant each: no case × variant rectangle, so two environments
    // (four planned executions) must still be refused, not accepted because 2 × 2 = 4.
    const text = JSON.stringify({
      config: {
        projects: [
          { id: 'a', name: 'a', metadata: { evidgate: { params: {} } } },
          { id: 'b', name: 'b', metadata: { evidgate: { params: { browser: 'firefox' } } } },
        ],
      },
      suites: [
        {
          file: 'tests/a.spec.ts',
          specs: [
            {
              title: 'one',
              tests: [
                {
                  projectId: 'a',
                  annotations: [{ type: 'evidgate.id', description: member(1).case_id }],
                },
              ],
            },
            {
              title: 'two',
              tests: [
                {
                  projectId: 'b',
                  annotations: [{ type: 'evidgate.id', description: member(2).case_id }],
                },
              ],
            },
          ],
        },
      ],
      errors: [],
    });
    const fixture = await setup({ text });
    const stub = stubFetch(() =>
      Response.json(page([member(1), member(2, { browser: 'firefox' })])),
    );

    const outcome = await run(fixture, stub.fetchImpl, [
      ...fixture.args,
      '--target-env',
      'e',
      '--target-env',
      'e-2',
    ]);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_plan_invalid: ');
    expect(outcome.stderr.join('')).toContain('4 execution(s) where 8 are needed');
    await expect(access(fixture.out)).rejects.toThrow();
  });

  it('a_manifest_over_the_32_mib_transport_cap_refuses_without_writing', async () => {
    // 2000 members x 32 environments of 64 characters, with 500-byte variant params: the
    // document is ~43 MiB, past the schema's own 32 MiB x-max-bytes transport cap.
    const params = { a: 'x'.repeat(250), b: 'y'.repeat(250) };
    const members = Array.from({ length: 2000 }, (_, index) => member(index + 1, params));
    const envs = Array.from({ length: 32 }, (_, index) =>
      `e${String(index).padStart(2, '0')}`.padEnd(64, 'x'),
    );
    const fixture = await setup({ text: report(members, params) });
    const stub = stubFetch((_call, index) => {
      const slice = members.slice(index * 500, (index + 1) * 500);
      const next = (index + 1) * 500 < members.length ? `page-${index + 1}` : null;
      return Response.json(page(slice, next));
    });

    const outcome = await run(fixture, stub.fetchImpl, [
      ...fixture.args,
      ...envs.flatMap((env) => ['--target-env', env]),
    ]);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_invalid: ');
    expect(outcome.stderr.join('')).toContain(
      'evidgate: hint: the manifest exceeds the 32 MiB manifest.v1 limit',
    );
    expect(outcome.stdout).toEqual([]);
    expect(stub.calls).toHaveLength(4);
    await expect(access(fixture.out)).rejects.toThrow();
  });

  it('a_document_past_the_engine_string_limit_is_refused_as_manifest_invalid', () => {
    // Allocating a >512 MiB document to reach the engine's own limit would dominate the run, so
    // the stub throws for the one object carrying `schema_version` and delegates everything else.
    const original = JSON.stringify;
    const stub = vi.spyOn(JSON, 'stringify').mockImplementation((value: unknown): string => {
      if (typeof value === 'object' && value !== null && 'schema_version' in value) {
        throw new RangeError('Invalid string length');
      }
      return original(value);
    });
    let refused: unknown;
    try {
      buildManifest(revision, [
        { caseRevisionId: '33333333-3333-4333-8333-000000000001', params: {} },
      ]);
    } catch (error) {
      refused = error;
    } finally {
      stub.mockRestore();
    }

    expect(refused).toBeInstanceOf(CliError);
    const error = refused as CliError;
    expect(error.code).toBe('manifest_invalid');
    expect(error.hint).toContain('the manifest exceeds the 32 MiB manifest.v1 limit');
  });
});

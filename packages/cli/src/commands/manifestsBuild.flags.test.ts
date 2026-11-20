// The flag refusals of `manifests build`: every malformed target, environment revision and
// shard count is refused before `getToken()`, so no token is read and no request is made; a
// resolved shard count above the execution count needs the plan, is refused after the read,
// and still creates no output file. A positive control proves the token spy observes the
// healthy path, so a "no token" assertion cannot pass by the spy being disconnected.
import { access } from 'node:fs/promises';

import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getToken } from '../auth/getToken.js';
import { resetSecrets } from '../redact.js';

import { member, page, report, stubFetch, useBuildSpec } from './manifestsBuild.testing.js';

import type * as GetTokenModule from '../auth/getToken.js';

vi.mock('../auth/getToken.js', async (importOriginal) => {
  const actual = await importOriginal<typeof GetTokenModule>();
  return { ...actual, getToken: vi.fn(actual.getToken) };
});

const tokenSpy = vi.mocked(getToken);
const environmentRevision = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';

const preTokenRefusals: { label: string; args: string[]; code: string }[] = [
  {
    label: 'an uppercase target env',
    args: ['--target-env', 'Prod'],
    code: 'manifest_target_env_invalid',
  },
  {
    label: 'a target env starting with a separator',
    args: ['--target-env=-e'],
    code: 'manifest_target_env_invalid',
  },
  { label: 'an empty target env', args: ['--target-env', ''], code: 'manifest_target_env_invalid' },
  {
    label: 'a 65-character target env',
    args: ['--target-env', 'e'.repeat(65)],
    code: 'manifest_target_env_invalid',
  },
  {
    label: 'a target env with a space',
    args: ['--target-env', 'e 2'],
    code: 'manifest_target_env_invalid',
  },
  {
    label: '33 distinct target envs',
    args: Array.from({ length: 33 }, (_, i) => ['--target-env', `e-${i}`]).flat(),
    code: 'manifest_target_env_invalid',
  },
  {
    label: 'a duplicate target env',
    args: ['--target-env', 'e', '--target-env', 'e'],
    code: 'manifest_target_env_duplicate',
  },
  {
    label: 'a non-uuid environment revision',
    args: ['--environment-revision-id', 'not-a-uuid'],
    code: 'manifest_environment_revision_invalid',
  },
  {
    label: 'a repeated environment revision',
    args: [
      '--environment-revision-id',
      environmentRevision,
      '--environment-revision-id',
      environmentRevision,
    ],
    code: 'usage',
  },
  { label: 'zero shards', args: ['--shards', '0'], code: 'manifest_shards_invalid' },
  { label: 'fractional shards', args: ['--shards', '1.5'], code: 'manifest_shards_invalid' },
  { label: 'word shards', args: ['--shards', 'abc'], code: 'manifest_shards_invalid' },
  { label: 'negative shards', args: ['--shards=-1'], code: 'manifest_shards_invalid' },
  {
    label: 'a space-separated negative shard count',
    args: ['--shards', '-1'],
    code: 'usage',
  },
  { label: 'exponent shards', args: ['--shards', '1e3'], code: 'manifest_shards_invalid' },
  { label: 'padded shards', args: ['--shards', ' 1'], code: 'manifest_shards_invalid' },
  {
    label: 'shards above the schema cap',
    args: ['--shards', '257'],
    code: 'manifest_shards_invalid',
  },
  {
    label: 'a repeated shard count',
    args: ['--shards', '1', '--shards', '1'],
    code: 'usage',
  },
];

describe('manifests build flag refusals', () => {
  const { setup, run } = useBuildSpec();

  beforeEach(() => {
    resetSecrets();
    tokenSpy.mockClear();
  });

  it('a_healthy_build_still_reads_the_token', async () => {
    const fixture = await setup();
    const stub = stubFetch(() => Response.json(page([member()], null)));

    expect((await run(fixture, stub.fetchImpl)).code).toBe(0);
    expect(tokenSpy).toHaveBeenCalledTimes(1);
  });

  it.each(preTokenRefusals)('$label makes no token call or request', async ({ args, code }) => {
    const fixture = await setup();
    const stub = stubFetch(() => Response.json(page()));

    const outcome = await run(fixture, stub.fetchImpl, [...fixture.args, ...args]);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain(`evidgate: error ${code}: `);
    expect(outcome.stdout).toEqual([]);
    expect(tokenSpy).not.toHaveBeenCalled();
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
    await expect(access(fixture.out)).rejects.toThrow();
  });

  it('a_shard_count_above_the_executions_writes_nothing', async () => {
    const members = [member(1), member(2)];
    const fixture = await setup({ text: report(members) });
    const stub = stubFetch(() => Response.json(page(members, null)));

    const outcome = await run(fixture, stub.fetchImpl, [...fixture.args, '--shards', '3']);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_shards_invalid: ');
    expect(outcome.stdout).toEqual([]);
    // The plan walk is the only request; the refusal precedes the output create.
    expect(stub.calls).toHaveLength(1);
    expect(tokenSpy).toHaveBeenCalledTimes(1);
    await expect(access(fixture.out)).rejects.toThrow();
  });

  it('a_multi_env_plan_counts_every_execution_for_the_shard_bound', async () => {
    const fixture = await setup({ text: report([member(1, {})], {}) });
    const stub = stubFetch(() => Response.json(page([member(1, {})], null)));

    const within = await run(fixture, stub.fetchImpl, [
      ...fixture.args,
      '--target-env',
      'e',
      '--target-env',
      'e-2',
      '--shards',
      '2',
    ]);

    expect(within.code).toBe(0);
  });
});

// `evidgate manifests build` on a healthy server: a three-page plan walk, the request shape
// (limit 500, the cursor from the previous page, the bearer), and the manifest the command
// writes — plan order, one matrix axis per plan variant, one shard over every execution.
import { readFile } from 'node:fs/promises';

import { beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import {
  bearer,
  member,
  page,
  report,
  revision,
  stubFetch,
  targetEnv,
  useBuildSpec,
} from './manifestsBuild.testing.js';

describe('manifests build', () => {
  const { setup, run } = useBuildSpec();
  const params = { browser: 'chromium' };

  beforeEach(() => {
    resetSecrets();
  });

  it('uppercase_revision_uses_the_canonical_server_identity', async () => {
    const canonical = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    const fixture = await setup();
    const args = [...fixture.args];
    args[3] = canonical.toUpperCase();
    const stub = stubFetch((call) => {
      expect(new URL(call.url).pathname).toContain(`/suite-revisions/${canonical}/`);
      return Response.json({ ...page(), suite_revision_id: canonical });
    });

    const outcome = await run(fixture, stub.fetchImpl, args);

    expect(outcome.code).toBe(0);
    expect(JSON.parse(await readFile(fixture.out, 'utf8'))).toMatchObject({
      suite_revision_id: canonical,
    });
    expect(outcome.stdout.join('')).toContain(`suite_revision_id=${canonical}\n`);
  });

  it('a_newline_in_the_output_path_cannot_forge_a_summary_line', async () => {
    const fixture = await setup();
    const out = `${fixture.out}\nforged=success`;
    const args = [...fixture.args];
    args[args.indexOf('--out') + 1] = out;
    const stub = stubFetch(() => Response.json(page()));

    const outcome = await run(fixture, stub.fetchImpl, args);

    expect(outcome.code).toBe(0);
    expect(outcome.stdout.join('')).toBe(
      `manifest=${fixture.out}\\u000aforged=success executions=1 suite_revision_id=${revision}\n`,
    );
    expect(await readFile(out, 'utf8')).toContain('"schema_version":1');
  });

  it('walks_every_page_and_writes_the_manifest', async () => {
    const members = [member(1, params), member(2, params), member(3, params), member(4, params)];
    const fixture = await setup({ text: report(members, params) });
    const pages = [
      page(members.slice(0, 2), 'cursor-1'),
      page(members.slice(2, 3), 'cursor-2'),
      page(members.slice(3), null),
    ];
    const stub = stubFetch((call, index) => {
      expect(new URL(call.url).pathname).toBe(`/v1/suite-revisions/${revision}/execution-plan`);
      expect(call.method).toBe('GET');
      expect(call.headers.get('authorization')).toBe(`Bearer ${bearer}`);
      expect(new URL(call.url).searchParams.get('limit')).toBe('500');
      const cursor = new URL(call.url).searchParams.get('cursor');
      expect(cursor).toBe(index === 0 ? null : `cursor-${index}`);
      return Response.json(pages[index]);
    });

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(0);
    expect(stub.calls).toHaveLength(3);
    expect(outcome.stdout.join('')).toBe(
      `manifest=${fixture.out} executions=4 suite_revision_id=${revision}\n`,
    );
    expect(outcome.stderr.join('')).toBe('');

    const text = await readFile(fixture.out, 'utf8');
    expect(JSON.parse(text)).toEqual({
      schema_version: 1,
      suite_revision_id: revision,
      planned_executions: members.map((m) => ({
        case_revision_id: m.case_revision_id,
        variant: { params },
        target_env: targetEnv,
      })),
      matrix: {
        case_revision_ids: members.map((m) => m.case_revision_id),
        variants: [{ params }],
        target_envs: [targetEnv],
      },
      shard_plan: {
        shards: [{ shard_id: 'shard-1', from_index: 0, to_index: 4 }],
      },
      env_needs: { target_envs: [{ target_env: targetEnv }] },
    });
    expect(text.endsWith('\n')).toBe(true);
    // Nothing is written under the project id, so the manifest is project-agnostic input.
    expect(text).not.toContain('proj-1');
    expect(stub.calls).toHaveLength(3);
  });

  it('prints_one_json_line', async () => {
    const fixture = await setup();
    const stub = stubFetch(() => Response.json(page([member()], null)));

    const outcome = await run(fixture, stub.fetchImpl, [...fixture.args, '--json']);

    expect(outcome.code).toBe(0);
    expect(outcome.stdout).toEqual([
      `${JSON.stringify({ manifest: fixture.out, executions: 1, suite_revision_id: revision })}\n`,
    ]);
    expect(outcome.stderr.join('')).toBe('');
  });

  it('reads_the_cached_token_without_an_exchange', async () => {
    const fixture = await setup();
    const stub = stubFetch(() => Response.json(page()));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(0);
    // The seeded cache answered `getToken()`, so the plan read is the only request.
    expect(stub.calls).toHaveLength(1);
    expect(stub.exchanges).toHaveLength(0);
  });
});

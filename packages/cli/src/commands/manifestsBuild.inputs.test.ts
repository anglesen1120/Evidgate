// The local-input refusals of `evidgate manifests build`: the mapping problems both ways, the
// duplicate local test, a variant the manifest format cannot express, flags that parse to
// nothing, and an output path that already exists or is a symlink.
import { mkdir, readFile, stat, symlink, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { jsonResponse } from './cliSpec.testing.js';
import { writeOutput } from './manifestsBuild.js';
import {
  bearer,
  member,
  page,
  report,
  revision,
  stubFetch,
  useBuildSpec,
} from './manifestsBuild.testing.js';

/** A report whose one spec carries the given `tests[]` verbatim. */
const customReport = (tests: unknown[], params: Record<string, unknown> = {}): string =>
  JSON.stringify({
    config: { projects: [{ id: 'p', name: 'chromium', metadata: { evidgate: { params } } }] },
    suites: [{ file: 'tests/a.spec.ts', specs: [{ title: 'a spec', tests }] }],
    errors: [],
  });

const annotation = (description: string) => ({ type: 'evidgate.id', description });

describe('manifests build input refusals', () => {
  const { setup, run, refusal: refuse } = useBuildSpec();

  beforeEach(() => {
    resetSecrets();
  });

  it.each([
    ['a missing suite revision', ['--playwright-list', 'LIST', '--out', 'OUT']],
    ['a non-uuid suite revision', ['--suite-revision', 'not-a-uuid']],
    ['a missing list report', ['--suite-revision', 'REV', '--out', 'OUT']],
    ['a missing output', ['--suite-revision', 'REV', '--playwright-list', 'LIST']],
    ['an empty output', ['--suite-revision', 'REV', '--playwright-list', 'LIST', '--out', '']],
  ])('bad flags (%s) make no request', async (_label, flags) => {
    const fixture = await setup({ cached: false });
    const args = [
      'manifests',
      'build',
      ...flags.map((flag) =>
        flag === 'LIST'
          ? fixture.list
          : flag === 'OUT'
            ? fixture.out
            : flag === 'REV'
              ? fixture.args[3]!
              : flag,
      ),
    ];
    const stub = stubFetch(() => jsonResponse(200, page()));

    const outcome = await run(fixture, stub.fetchImpl, args);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error usage: ');
    // Neither the plan read nor the token exchange ran: the flags were refused first.
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
  });

  it('an_unreadable_list_report_makes_no_request', async () => {
    const fixture = await setup({ cached: false });
    const stub = stubFetch(() => jsonResponse(200, page()));
    const missing = join(fixture.root, 'absent.json');
    const args = [...fixture.args];
    args[args.indexOf('--playwright-list') + 1] = missing;

    const outcome = await run(fixture, stub.fetchImpl, args);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_report_unreadable: ');
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
  });

  it('an_unmapped_test_refuses_uplike_the_mapping_rule', async () => {
    const fixture = await setup({ text: customReport([{ projectId: 'p' }]) });
    await refuse('manifest_test_unmapped', () => jsonResponse(200, page()), fixture);
  });

  it('a_conflicting_annotation_is_ambiguous', async () => {
    const first = member(1).case_id;
    const second = member(2).case_id;
    const fixture = await setup({
      text: customReport([
        { projectId: 'p', annotations: [annotation(first), annotation(second)] },
      ]),
    });
    await refuse('manifest_test_ambiguous', () => jsonResponse(200, page([member(1)])), fixture);
  });

  it('two_local_tests_with_one_identity_are_duplicates', async () => {
    const id = member(1).case_id;
    const fixture = await setup({
      text: customReport([
        { projectId: 'p', annotations: [annotation(id)] },
        { projectId: 'p', annotations: [annotation(id)] },
      ]),
    });
    const { outcome } = await refuse(
      'manifest_test_duplicate',
      () => jsonResponse(200, page([member(1)])),
      fixture,
    );
    expect(outcome.stderr.join('')).toContain('tests/a.spec.ts');
    // One `detail:` line per offending test, both naming the spec file and its title.
    expect(outcome.stderr.join('')).toContain('a spec');
  });

  it('a_plan_member_with_no_local_test_is_unexercised', async () => {
    const fixture = await setup({ text: report([member(1)]) });
    const { outcome } = await refuse(
      'manifest_plan_member_unexercised',
      () => jsonResponse(200, page([member(1), member(2)])),
      fixture,
    );
    expect(outcome.stderr.join('')).toContain(member(2).case_id);
  });

  it('a_non_string_variant_param_is_unrepresentable', async () => {
    const params = { retries: 2 };
    const fixture = await setup({ text: report([member(1, params)], params) });
    const { outcome } = await refuse(
      'manifest_variant_unrepresentable',
      () => jsonResponse(200, page([member(1, params)])),
      fixture,
    );
    expect(outcome.stderr.join('')).toContain('tests/a.spec.ts');
  });

  it('variant_refusal_redacts_a_bearer_before_clipping_the_title', async () => {
    const params = { retries: 2 };
    const text = report([member(1, params)], params).replace(
      '"title":"case-0"',
      `"title":${JSON.stringify('x'.repeat(188) + bearer)}`,
    );
    expect(text).toContain('x'.repeat(188) + bearer);
    const fixture = await setup({ text });
    const { outcome } = await refuse(
      'manifest_variant_unrepresentable',
      () => jsonResponse(200, page([member(1, params)])),
      fixture,
    );
    expect(outcome.stderr.join('')).toContain('[REDACTED]');
    expect(outcome.stderr.join('')).not.toContain(bearer.slice(0, 12));
  });

  it('a_relative_report_path_in_a_subdirectory_is_read', async () => {
    // The report path reaches the reader already resolved; resolving it again against the
    // containment root would look for `reports/reports/list.json`.
    const fixture = await setup();
    const nested = join(fixture.root, 'reports');
    await mkdir(nested, { recursive: true });
    await writeFile(join(nested, 'list.json'), report());
    const stub = stubFetch(() => jsonResponse(200, page()));
    const previous = process.cwd();
    process.chdir(fixture.root);
    try {
      const outcome = await run(fixture, stub.fetchImpl, [
        'manifests',
        'build',
        '--suite-revision',
        revision,
        '--playwright-list',
        'reports/list.json',
        '--out',
        join(fixture.root, 'nested-manifest.json'),
      ]);
      expect(outcome.code).toBe(0);
      expect(stub.calls).toHaveLength(1);
    } finally {
      process.chdir(previous);
    }
  });

  it('a_plan_that_is_not_a_full_matrix_is_refused', async () => {
    // One case with the empty variant and another with `browser=firefox`: no case × variant
    // rectangle covers both, so no manifest can describe this revision.
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
              tests: [{ projectId: 'a', annotations: [annotation(member(1).case_id)] }],
            },
            {
              title: 'two',
              tests: [{ projectId: 'b', annotations: [annotation(member(2).case_id)] }],
            },
          ],
        },
      ],
      errors: [],
    });
    const fixture = await setup({ text });
    const plan = page([member(1), member(2, { browser: 'firefox' })]);
    const stub = stubFetch(() => jsonResponse(200, plan));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_plan_invalid: ');
    expect(outcome.stderr.join('')).toContain('not a full matrix');
  });

  it('an_existing_output_is_refused_and_kept', async () => {
    const fixture = await setup();
    await writeFile(fixture.out, 'keep me');
    const stub = stubFetch(() => jsonResponse(200, page()));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_output_exists: ');
    await expect(readFile(fixture.out, 'utf8')).resolves.toBe('keep me');
  });

  it('a_symlinked_output_is_refused_without_touching_its_target', async () => {
    const fixture = await setup();
    const target = join(fixture.root, 'target.json');
    await writeFile(target, 'target');
    await symlink(target, fixture.out);
    const stub = stubFetch(() => jsonResponse(200, page()));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_output_exists: ');
    await expect(readFile(target, 'utf8')).resolves.toBe('target');
  });

  it('an_unwritable_output_directory_is_refused', async () => {
    const fixture = await setup();
    const nested = join(fixture.root, 'absent', 'manifest.json');
    const args = [...fixture.args];
    args[args.indexOf('--out') + 1] = nested;
    const stub = stubFetch(() => jsonResponse(200, page()));

    const outcome = await run(fixture, stub.fetchImpl, args);

    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain('evidgate: error manifest_output_unwritable: ');
  });

  it('a_failed_write_after_the_create_removes_the_partial_file', async () => {
    const fixture = await setup();
    const path = join(fixture.root, 'partial.json');
    const failing = async (target: string, text: string): Promise<void> => {
      await writeFile(target, text, { flag: 'wx', mode: 0o600 });
      throw Object.assign(new Error('no space left'), { code: 'ENOSPC' });
    };

    await expect(writeOutput(path, 'partial', failing)).rejects.toMatchObject({
      code: 'manifest_output_unwritable',
    });
    await expect(stat(path)).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('a_failed_write_removes_the_entry_name_without_following_a_symlink', async () => {
    const fixture = await setup();
    const target = join(fixture.root, 'kept.json');
    const path = join(fixture.root, 'swapped.json');
    await writeFile(target, 'keep me');
    const swapped = async (): Promise<void> => {
      await symlink(target, path);
      throw Object.assign(new Error('no space left'), { code: 'ENOSPC' });
    };

    await expect(writeOutput(path, 'partial', swapped)).rejects.toMatchObject({
      code: 'manifest_output_unwritable',
    });
    await expect(stat(path)).rejects.toMatchObject({ code: 'ENOENT' });
    await expect(readFile(target, 'utf8')).resolves.toBe('keep me');
  });

  it('the_output_is_owner_only', async () => {
    const fixture = await setup();
    const stub = stubFetch(() => jsonResponse(200, page()));

    const outcome = await run(fixture, stub.fetchImpl);

    expect(outcome.code).toBe(0);
    const mode = (await stat(fixture.out)).mode & 0o777;
    expect(mode & 0o077).toBe(0);
  });
});

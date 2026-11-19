import { execFileSync } from 'node:child_process';
import * as fsModule from 'node:fs/promises';
import { link, mkdtemp, mkdir, rm, symlink, truncate, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { readFileBounded } from './boundedFile.js';
import { CliError } from './errors.js';
import { mapTestsToMembers } from './manifestMapping.js';
import { parsePlaywrightList, readPlaywrightList } from './playwrightList.js';

// One-shot seam for the path-swap race: the guard reads the identity after the open, so a
// test replaces the file at exactly that point instead of racing a real writer.
const swapHook = vi.hoisted(() => ({
  once: undefined as undefined | (() => Promise<void>),
}));

vi.mock('node:fs/promises', async (importOriginal) => {
  const actual = await importOriginal<typeof fsModule>();
  return {
    ...actual,
    lstat: (...args: Parameters<typeof actual.lstat>) => {
      const swap = swapHook.once;
      swapHook.once = undefined;
      return swap === undefined ? actual.lstat(...args) : swap().then(() => actual.lstat(...args));
    },
  };
});

const caseId = '12345678-1234-1234-1234-123456789abc';
const dirs: string[] = [];
const hashOf = (params: unknown): string => `hash:${JSON.stringify(params)}`;
const project = (id = 'p', params: unknown = {}): object => ({
  id,
  name: id,
  metadata: { evidgate: { params } },
});
const listedTest = (id = 'p'): object => ({
  projectId: id,
  projectName: id,
  annotations: [{ type: 'evidgate.id', description: caseId }],
});
const suite = (
  file = 'tests/a.spec.ts',
  title = 'works',
  tests: unknown = [listedTest()],
): object => ({ file, title: file, specs: [{ title, tests }] });
const report = (
  suites: unknown = [suite()],
  projects: unknown = [project()],
  errors: unknown = [],
): string => JSON.stringify({ config: { projects }, suites, errors });

async function tempDir(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'evidgate-manifest-boundary-'));
  dirs.push(dir);
  return dir;
}

afterEach(async () => {
  swapHook.once = undefined;
  vi.restoreAllMocks();
  await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
});

function nested(depth: number): object {
  let child: object = suite();
  for (let i = 1; i < depth; i++) {
    child = { title: `describe-${i}`, file: 'tests/a.spec.ts', suites: [child] };
  }
  return child;
}

function mapped(text: string): number {
  const tests = parsePlaywrightList(text, hashOf);
  const plan = {
    suite_revision_id: 'suite',
    members: [{ case_id: caseId, variant_hash: 'hash:{}', case_revision_id: 'rev' }],
  };
  return mapTestsToMembers(tests, plan).entries.length;
}

function refusal(text: string): CliError {
  try {
    parsePlaywrightList(text, hashOf);
  } catch (error) {
    if (error instanceof CliError) return error;
    throw error;
  }
  throw new Error('expected the report to be refused');
}

describe('parsePlaywrightList hostile boundaries', () => {
  it('realistic_report_maps_one_test', () => {
    expect(mapped(report())).toBe(1);
  });

  it('zero_tests_refuses_instead_of_mapping_an_empty_suite', () => {
    expect(mapped(report())).toBe(1);
    const error = refusal(report([]));
    expect(error.code).toBe('manifest_report_invalid');
    expect(error.message).toContain('no tests');
  });

  it('huge_unicode_title_preserves_identity_and_mapping', () => {
    const title = '越'.repeat(350_000);
    const tests = parsePlaywrightList(report([suite('tests/越.spec.ts', title)]), hashOf);
    expect(tests[0]?.titlePath).toEqual([title]);
    expect(tests[0]?.file).toBe('tests/越.spec.ts');
    expect(tests[0]?.caseId).toBe(caseId);
  });

  it('suite_depth_32_succeeds_and_33_refuses_without_stack_overflow', () => {
    expect(parsePlaywrightList(report([nested(32)]), hashOf)).toHaveLength(1);
    expect(() => parsePlaywrightList(report([nested(33)]), hashOf)).toThrow(CliError);
  });

  it('prototype_keys_in_metadata_are_data_without_global_pollution', () => {
    const params: unknown = JSON.parse(
      '{"__proto__":{"polluted":true},"constructor":{"prototype":{"polluted":true}}}',
    );
    const text = report([suite()], [project('p', params)]);
    expect(parsePlaywrightList(text, hashOf)[0]?.variantHash).toBe(hashOf(params));
    expect(Object.prototype).not.toHaveProperty('polluted');
  });

  it.each(['__proto__', 'constructor', 'toString'])(
    'prototype_shaped_project_id_%s_is_safe',
    (id) => {
      const text = report([suite('tests/a.spec.ts', 'works', [listedTest(id)])], [project(id)]);
      expect(parsePlaywrightList(text, hashOf)[0]?.caseId).toBe(caseId);
    },
  );

  it('collection_errors_refuse_without_echoing_error_content', () => {
    expect(mapped(report())).toBe(1);
    for (const suites of [[], [suite()]]) {
      const error = refusal(
        report(
          suites,
          [project()],
          [{ message: 'private-collection-error' }, { stack: 'private-stack' }],
        ),
      );
      expect(error.code).toBe('manifest_report_invalid');
      expect(error.message).toContain('2 collection errors');
      expect(JSON.stringify(error)).not.toContain('private-');
    }
  });

  it.each([null, {}, 'lost suites', 0])(
    'present_malformed_suites_%j_refuses_even_beside_valid_tests',
    (suites) => {
      expect(mapped(report([{ ...suite(), suites: [] }]))).toBe(1);
      expect(refusal(report([{ ...suite(), suites }])).code).toBe('manifest_report_invalid');
      expect(refusal(report(suites)).code).toBe('manifest_report_invalid');
    },
  );

  it.each([null, {}, 'lost specs', 0])(
    'present_malformed_specs_%j_refuses_even_beside_valid_suites',
    (specs) => {
      expect(mapped(report([suite(), { file: 'tests/empty.spec.ts', specs: [] }]))).toBe(1);
      const malformed = { file: 'tests/empty.spec.ts', specs };
      expect(refusal(report([suite(), malformed])).code).toBe('manifest_report_invalid');
    },
  );

  it.each([null, {}, 'lost tests', 0])(
    'present_malformed_tests_%j_refuses_even_beside_valid_specs',
    (tests) => {
      expect(mapped(report([suite(), suite('tests/empty.spec.ts', 'empty', [])]))).toBe(1);
      expect(refusal(report([suite(), suite('tests/empty.spec.ts', 'empty', tests)])).code).toBe(
        'manifest_report_invalid',
      );
    },
  );

  it('absent_optional_collection_keys_are_empty_beside_a_real_test', () => {
    const emptySuite = { file: 'tests/empty.spec.ts' };
    const emptySpec = { file: 'tests/empty.spec.ts', specs: [{ title: 'not collected' }] };
    expect(mapped(report([suite(), emptySuite, emptySpec]))).toBe(1);
  });

  it('duplicate_project_id_with_distinct_variants_refuses_ambiguity', () => {
    expect(parsePlaywrightList(report(), hashOf)).toHaveLength(1);
    const text = report([suite()], [project('p', { x: 1 }), project('p', { x: 2 })]);
    expect(refusal(text).code).toBe('manifest_report_invalid');
  });

  it('unknown_project_id_cannot_fall_back_to_an_existing_project_name', () => {
    expect(parsePlaywrightList(report(), hashOf)).toHaveLength(1);
    const text = report([
      suite('tests/a.spec.ts', 'works', [{ ...listedTest(), projectId: 'unknown' }]),
    ]);
    expect(refusal(text).code).toBe('manifest_variant_invalid');
  });

  it.each(['\u0000', '\n', '\r', '\t', '\u001f'])(
    'control_character_%j_in_spec_path_refuses_invalid_report',
    (control) => {
      expect(parsePlaywrightList(report([suite('tests/越.spec.ts')]), hashOf)[0]?.file).toBe(
        'tests/越.spec.ts',
      );
      expect(refusal(report([suite(`tests/a${control}.spec.ts`)])).code).toBe(
        'manifest_report_invalid',
      );
    },
  );

  it('relative_colon_path_succeeds_but_drive_absolute_paths_refuse', () => {
    expect(parsePlaywrightList(report([suite('a:relative.spec.ts')]), hashOf)[0]?.file).toBe(
      'a:relative.spec.ts',
    );
    for (const file of ['C:/repo/test.spec.ts', 'C:\\repo\\test.spec.ts']) {
      expect(refusal(report([suite(file)])).code).toBe('manifest_path_outside_root');
    }
  });

  it('duplicate_project_id_refuses_even_with_identical_params', () => {
    expect(mapped(report())).toBe(1);
    expect(refusal(report([suite()], [project(), project()])).code).toBe('manifest_report_invalid');
  });

  it('duplicate_project_name_requires_equal_params_hashes', () => {
    const alias = { ...project('alias'), name: 'p' };
    expect(mapped(report([suite()], [project(), alias]))).toBe(1);
    const conflicting = { ...project('alias', { different: true }), name: 'p' };
    expect(refusal(report([suite()], [project(), conflicting])).code).toBe(
      'manifest_report_invalid',
    );
  });

  it('name_fallback_requires_an_absent_project_id', () => {
    const byName = {
      projectName: 'p',
      annotations: [{ type: 'evidgate.id', description: caseId }],
    };
    expect(mapped(report([suite('tests/a.spec.ts', 'works', [byName])]))).toBe(1);
    const unknown = report([
      suite('tests/a.spec.ts', 'works', [{ ...byName, projectId: 'unknown' }]),
    ]);
    expect(refusal(unknown).code).toBe('manifest_variant_invalid');
    for (const projectId of [null, 0]) {
      const malformed = report([suite('tests/a.spec.ts', 'works', [{ ...byName, projectId }])]);
      expect(refusal(malformed).code).toBe('manifest_report_invalid');
    }
  });

  it('id_and_name_cannot_resolve_to_different_projects_even_with_equal_hashes', () => {
    const projects = [project(), project('other')];
    expect(mapped(report([suite()], projects))).toBe(1);
    const conflicting = { ...listedTest(), projectName: 'other' };
    expect(refusal(report([suite('tests/a.spec.ts', 'works', [conflicting])], projects)).code).toBe(
      'manifest_report_invalid',
    );
  });

  it('one_thousand_projects_succeed_and_one_more_refuses_before_hashing', () => {
    const projects = [project(), ...Array.from({ length: 999 }, (_, i) => project(`p${i}`))];
    expect(mapped(report([suite()], projects))).toBe(1);
    const hash = vi.fn(hashOf);
    expect(() =>
      parsePlaywrightList(report([suite()], [...projects, project('extra')]), hash),
    ).toThrow(expect.objectContaining({ code: 'manifest_input_too_large' }));
    expect(hash).not.toHaveBeenCalled();
  });

  it('duplicate_members_even_with_same_revision_refuse', () => {
    const tests = parsePlaywrightList(report(), hashOf);
    const member = { case_id: caseId, variant_hash: 'hash:{}', case_revision_id: 'rev' };
    expect(
      mapTestsToMembers(tests, { suite_revision_id: 'suite', members: [member] }).entries,
    ).toHaveLength(1);
    expect(() =>
      mapTestsToMembers(tests, { suite_revision_id: 'suite', members: [member, member] }),
    ).toThrow(CliError);
  });
});

describe('readPlaywrightList and readFileBounded filesystem boundaries', () => {
  it('unicode_report_filename_reads_and_nul_filename_refuses', async () => {
    const root = await tempDir();
    await writeFile(join(root, '越.json'), report());
    await expect(readPlaywrightList('越.json', root)).resolves.toBe(report());
    await expect(readPlaywrightList('越\u0000.json', root)).rejects.toMatchObject({
      code: 'manifest_report_unreadable',
    });
  });

  it('contained_symlink_reads_and_directory_symlink_escape_refuses', async () => {
    const root = await tempDir();
    const outside = await tempDir();
    await writeFile(join(root, 'report.json'), report());
    await writeFile(join(outside, 'report.json'), report());
    await symlink(join(root, 'report.json'), join(root, 'inside.json'));
    await symlink(outside, join(root, 'outside'));
    await expect(readPlaywrightList('inside.json', root)).resolves.toBe(report());
    await expect(readPlaywrightList('outside/report.json', root)).rejects.toMatchObject({
      code: 'manifest_path_outside_root',
    });
  });

  it('root_symlink_is_resolved_without_false_escape', async () => {
    const parent = await tempDir();
    await mkdir(join(parent, 'real'));
    await writeFile(join(parent, 'real', 'report.json'), report());
    await symlink(join(parent, 'real'), join(parent, 'alias'));
    await expect(readPlaywrightList('report.json', join(parent, 'alias'))).resolves.toBe(report());
  });

  it('empty_file_is_invalid_not_successful_empty_report', async () => {
    const root = await tempDir();
    await writeFile(join(root, 'report.json'), report());
    await expect(readPlaywrightList('report.json', root)).resolves.toBe(report());
    await writeFile(join(root, 'empty.json'), '');
    await expect(readPlaywrightList('empty.json', root)).rejects.toMatchObject({
      code: 'manifest_report_invalid',
    });
  });

  it('bounded_reader_exact_one_byte_succeeds_and_overflow_refuses', async () => {
    const root = await tempDir();
    const file = join(root, 'bytes');
    await writeFile(file, 'a');
    const atCap = await readFileBounded(file, 1);
    expect(atCap.ok).toBe(true);
    if (atCap.ok) expect(Buffer.from(atCap.bytes).toString()).toBe('a');
    await writeFile(file, 'ab');
    await expect(readFileBounded(file, 1)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'too_large' },
    });
  });

  it('bounded_reader_empty_zero_cap_missing_directory_and_nul_are_typed_failures', async () => {
    const root = await tempDir();
    const file = join(root, 'bytes');
    await writeFile(file, 'a');
    await expect(readFileBounded(file, 0)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'too_large' },
    });
    await writeFile(file, '');
    await expect(readFileBounded(file, 1)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'empty' },
    });
    await expect(readFileBounded(root, 1)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'not_regular_file' },
    });
    await expect(readFileBounded(join(root, 'missing'), 1)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'unreadable', errno: 'ENOENT' },
    });
    await expect(readFileBounded(`${file}\u0000`, 1)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'unreadable' },
    });
  });

  it('fifo_refuses_promptly_without_a_writer', async () => {
    const root = await tempDir();
    const path = join(root, 'pipe');
    execFileSync('mkfifo', [path]);
    await expect(readFileBounded(path, 1)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'not_regular_file' },
    });
  });

  it('oversized_stat_refuses_before_any_buffer_and_exact_cap_reads', async () => {
    const root = await tempDir();
    const sparse = join(root, 'sparse');
    await writeFile(sparse, 'a');
    await truncate(sparse, 4096);
    await expect(readFileBounded(sparse, 8)).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'too_large' },
    });
    const atCap = join(root, 'at-cap');
    await writeFile(atCap, '12345678');
    const result = await readFileBounded(atCap, 8);
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.bytes).toHaveLength(8);
      expect(Buffer.from(result.bytes).toString()).toBe('12345678');
    }
  });

  it('no_follow_refuses_a_symlink_while_the_default_still_follows_it', async () => {
    const root = await tempDir();
    const target = join(root, 'report.json');
    const link = join(root, 'link.json');
    await writeFile(target, report());
    await symlink(target, link);
    const followed = await readFileBounded(link, 1024);
    expect(followed.ok).toBe(true);
    await expect(readFileBounded(link, 1024, { noFollow: true })).resolves.toMatchObject({
      ok: false,
      failure: { kind: 'unreadable', errno: 'ELOOP' },
    });
  });

  it('report_replaced_between_open_and_identity_check_refuses', async () => {
    const root = await tempDir();
    const file = join(root, 'report.json');
    await writeFile(file, report());
    await expect(readPlaywrightList('report.json', root)).resolves.toBe(report());
    swapHook.once = async () => {
      // Keep the old inode alive: a freed inode number may be reused by the replacement.
      await link(file, `${file}.old`);
      await rm(file);
      await writeFile(file, report());
    };
    await expect(readPlaywrightList('report.json', root)).rejects.toMatchObject({
      code: 'manifest_report_unreadable',
    });
    await expect(readPlaywrightList('report.json', root)).resolves.toBe(report());
  });
});

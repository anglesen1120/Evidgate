import { mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import { MAX_TESTS, mapTestsToMembers, type PlanMember } from './manifestMapping.js';
import { MAX_REPORT_BYTES, parsePlaywrightList, readPlaywrightList } from './playwrightList.js';

const caseId = '12345678-1234-1234-1234-123456789abc';
const dirs: string[] = [];
/** The 16 MiB fixture is built once, on first use: two specs and two runs need the same one. */
let byteCapFixture: string | undefined;

afterEach(async () => {
  await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
});

async function tempDir(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-list-'));
  dirs.push(dir);
  return dir;
}

/** A stable stand-in for the adapter's variant hash: same params, same hash. */
function hashOf(params: unknown): string | undefined {
  if (typeof params !== 'object' || params === null || Array.isArray(params)) {
    return undefined;
  }
  return `hash:${JSON.stringify(params)}`;
}

function project(id: string, name: string, params?: unknown): unknown {
  return {
    id,
    name,
    metadata: params === undefined ? {} : { evidgate: { params } },
  };
}

function testCase(projectId: string, annotations?: unknown[]): unknown {
  return { projectId, projectName: `name-${projectId}`, annotations: annotations ?? [] };
}

function spec(title: string, tests: unknown[]): unknown {
  return { title, tests };
}

function fileSuite(file: string, specs: unknown[], suites?: unknown[]): unknown {
  return { title: file, file, specs, ...(suites === undefined ? {} : { suites }) };
}

function report(projects: unknown[], suites: unknown[]): string {
  return JSON.stringify({
    config: { rootDir: '/repo', projects },
    suites,
    errors: [],
    stats: {},
  });
}

function singleTestReport(params: unknown, annotations: unknown[]): string {
  return report(
    [project('p1', 'chrome', params)],
    [fileSuite('tests/checkout.spec.ts', [spec('pays', [testCase('p1', annotations)])])],
  );
}

/** One spec carrying `count` tests, used for the report's own test-count cap. */
function manyTestReport(count: number): string {
  const tests = Array.from({ length: count }, () => testCase('p1'));
  return report(
    [project('p1', 'chrome', {})],
    [fileSuite('tests/many.spec.ts', [spec('works', tests)])],
  );
}

/** One report padded with spaces to exactly the byte cap; built once for the whole file. */
function atCapReport(): string {
  if (byteCapFixture === undefined) {
    const base = singleTestReport({}, []);
    byteCapFixture = base + ' '.repeat(MAX_REPORT_BYTES - Buffer.byteLength(base, 'utf8'));
  }
  return byteCapFixture;
}

function parseRefusal(text: string): CliError {
  try {
    parsePlaywrightList(text, hashOf);
  } catch (error) {
    expect(error).toBeInstanceOf(CliError);
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('the parser returned tests where a refusal was expected');
}

describe('parsePlaywrightList', () => {
  it('enumerates_identity_variant_and_project_relative_path', () => {
    const annotated = testCase('p1', [{ type: 'evidgate.id', description: caseId.toUpperCase() }]);
    const chrome = project('p1', 'chrome', { plan: 'pro' });
    const text = report(
      [chrome],
      [
        fileSuite(
          'tests/checkout.spec.ts',
          [],
          [{ title: 'checkout', specs: [spec('pays', [annotated])] }],
        ),
      ],
    );
    expect(parsePlaywrightList(text, hashOf)).toEqual([
      {
        file: 'tests/checkout.spec.ts',
        titlePath: ['checkout', 'pays'],
        caseId,
        variantHash: 'hash:{"plan":"pro"}',
      },
    ]);
  });

  it('missing_or_noncanonical_annotation_yields_one_test_without_identity', () => {
    const absent = report(
      [project('p1', 'chrome', {})],
      [fileSuite('tests/a.spec.ts', [spec('works', [{ projectId: 'p1' }])])],
    );
    const invalid = singleTestReport({}, [{ type: 'evidgate.id', description: 'not-a-uuid' }]);
    const annotated = singleTestReport({}, [{ type: 'evidgate.id', description: caseId }]);
    for (const text of [absent, invalid]) {
      const tests = parsePlaywrightList(text, hashOf);
      expect(tests).toHaveLength(1);
      expect(tests[0]?.caseId).toBeUndefined();
    }
    expect(parsePlaywrightList(annotated, hashOf)[0]?.caseId).toBe(caseId);
  });

  it('duplicate_annotation_in_another_case_is_one_identity', () => {
    const text = singleTestReport({}, [
      { type: 'evidgate.id', description: caseId },
      { type: 'evidgate.id', description: caseId.toUpperCase() },
    ]);
    const [enumerated] = parsePlaywrightList(text, hashOf);
    expect(enumerated?.caseId).toBe(caseId);
    expect(enumerated?.identityConflict).toBeUndefined();
  });

  it('two_distinct_annotations_mark_a_conflict_without_identity', () => {
    const text = singleTestReport({}, [
      { type: 'evidgate.id', description: caseId },
      { type: 'evidgate.id', description: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' },
    ]);
    const [enumerated] = parsePlaywrightList(text, hashOf);
    expect(enumerated?.caseId).toBeUndefined();
    expect(enumerated?.identityConflict).toBe(true);
  });

  it('valid_annotation_beside_invalid_one_keeps_the_identity', () => {
    const text = singleTestReport({}, [
      { type: 'evidgate.id', description: caseId },
      { type: 'evidgate.id', description: 'urn:uuid:nope' },
    ]);
    expect(parsePlaywrightList(text, hashOf)[0]?.caseId).toBe(caseId);
  });

  it('same_spec_under_two_projects_maps_independently', () => {
    const text = report(
      [project('p1', 'chrome', { plan: 'pro' }), project('p2', 'firefox', { plan: 'free' })],
      [
        fileSuite('tests/shared.spec.ts', [
          spec('works', [
            testCase('p1', [{ type: 'evidgate.id', description: caseId }]),
            testCase('p2', [{ type: 'evidgate.id', description: caseId }]),
          ]),
        ]),
      ],
    );
    const tests = parsePlaywrightList(text, hashOf);
    expect(tests.map((entry) => entry.variantHash)).toEqual([
      'hash:{"plan":"pro"}',
      'hash:{"plan":"free"}',
    ]);
    const members: PlanMember[] = tests.map((entry, index) => ({
      case_id: caseId,
      variant_hash: entry.variantHash,
      case_revision_id: `rev-${index}`,
    }));
    const result = mapTestsToMembers(tests, { suite_revision_id: 'suite-1', members });
    expect(result.entries.map((entry) => entry.caseRevisionId)).toEqual(['rev-0', 'rev-1']);
  });

  it('project_params_without_a_hash_refuse_variant_invalid', () => {
    const text = singleTestReport('secret-parameter-value', []);
    const error = parseRefusal(text);
    expect(error.code).toBe('manifest_variant_invalid');
    expect(error.message).not.toContain('secret-parameter-value');
  });

  it('test_in_an_unknown_project_refuses_variant_invalid', () => {
    const text = report(
      [project('p1', 'chrome', {})],
      [fileSuite('tests/a.spec.ts', [spec('works', [testCase('missing-project')])])],
    );
    expect(parseRefusal(text).code).toBe('manifest_variant_invalid');
  });

  it('spec_file_escaping_the_root_refuses_path_outside_root', () => {
    const escaping = [
      '../../etc/passwd',
      '/etc/passwd',
      'C:\\repo\\x.spec.ts',
      'a/../../x.spec.ts',
    ];
    for (const file of escaping) {
      const text = report(
        [project('p1', 'chrome', {})],
        [fileSuite(file, [spec('works', [testCase('p1')])])],
      );
      expect(parseRefusal(text).code).toBe('manifest_path_outside_root');
    }
  });

  it('nested_spec_file_is_normalised_to_posix_relative', () => {
    const text = report(
      [project('p1', 'chrome', {})],
      [fileSuite('tests/./nested/../a.spec.ts', [spec('works', [testCase('p1')])])],
    );
    expect(parsePlaywrightList(text, hashOf)[0]?.file).toBe('tests/a.spec.ts');
  });

  it('malformed_report_refuses_report_invalid', () => {
    expect(parseRefusal('not json').code).toBe('manifest_report_invalid');
    expect(parseRefusal(JSON.stringify({ config: { projects: [] } })).code).toBe(
      'manifest_report_invalid',
    );
  });

  it('tests_at_the_cap_parse_and_one_over_refuses_too_large', () => {
    expect(parsePlaywrightList(manyTestReport(MAX_TESTS), hashOf)).toHaveLength(MAX_TESTS);
    expect(parseRefusal(manyTestReport(MAX_TESTS + 1)).code).toBe('manifest_input_too_large');
  });

  it('report_at_the_byte_cap_parses_and_one_over_refuses_too_large', () => {
    const atCap = atCapReport();
    expect(Buffer.byteLength(atCap, 'utf8')).toBe(MAX_REPORT_BYTES);
    expect(parsePlaywrightList(atCap, hashOf)).toHaveLength(1);
    expect(parseRefusal(`${atCap} `).code).toBe('manifest_input_too_large');
  });
});

describe('readPlaywrightList', () => {
  it('reads_a_report_inside_the_root', async () => {
    const root = await tempDir();
    const text = singleTestReport({}, []);
    await writeFile(join(root, 'report.json'), text);
    await expect(readPlaywrightList('report.json', root)).resolves.toBe(text);
  });

  it('traversal_absolute_and_symlink_escape_refuse_outside_root', async () => {
    const root = await tempDir();
    const outside = await tempDir();
    const outsideFile = join(outside, 'report.json');
    await writeFile(outsideFile, singleTestReport({}, []));
    await symlink(outsideFile, join(root, 'link.json'));

    await expect(readPlaywrightList(relative(root, outsideFile), root)).rejects.toMatchObject({
      code: 'manifest_path_outside_root',
    });
    await expect(readPlaywrightList('../missing.json', root)).rejects.toMatchObject({
      code: 'manifest_path_outside_root',
    });
    await expect(readPlaywrightList(outsideFile, root)).rejects.toMatchObject({
      code: 'manifest_path_outside_root',
    });
    await expect(readPlaywrightList('link.json', root)).rejects.toMatchObject({
      code: 'manifest_path_outside_root',
    });
  });

  it('file_at_the_byte_cap_reads_and_one_over_refuses_too_large', async () => {
    const root = await tempDir();
    const atCap = atCapReport();
    await writeFile(join(root, 'at-cap.json'), atCap);
    await writeFile(join(root, 'over-cap.json'), `${atCap} `);
    const text = await readPlaywrightList('at-cap.json', root);
    expect(text).toHaveLength(atCap.length);
    expect(text.startsWith(singleTestReport({}, []))).toBe(true);
    await expect(readPlaywrightList('over-cap.json', root)).rejects.toMatchObject({
      code: 'manifest_input_too_large',
    });
  });

  it('invalid_utf8_bytes_refuse_report_invalid', async () => {
    const root = await tempDir();
    await writeFile(join(root, 'latin1.json'), Buffer.from([0x7b, 0xff, 0x7d]));
    await expect(readPlaywrightList('latin1.json', root)).rejects.toMatchObject({
      code: 'manifest_report_invalid',
    });
  });
});

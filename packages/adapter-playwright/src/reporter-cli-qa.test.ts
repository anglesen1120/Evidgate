// Independent QA: real Playwright CLI, using the shipped e2e fixture config.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { Ajv2020 } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { computeExecutionKey } from './executionKey.js';
import type { WarningPayload } from './identity.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const packageDir = join(root, 'packages/adapter-playwright');
const config = join(packageDir, 'test-fixtures/playwright.config.mjs');
const cli = join(root, 'node_modules/@playwright/test/cli.js');
const directory = mkdtempSync(join(tmpdir(), 'evidgate-cli-qa-'));
const ajv = new Ajv2020({ strict: false, allErrors: true });
ajvFormats.default(ajv);
const schema = JSON.parse(
  readFileSync(join(root, 'contracts/schemas/event-envelope.v1.json'), 'utf8'),
) as Record<string, unknown>;
schema['$id'] = 'urn:evidgate:qa:event-envelope';
const validate = ajv.compile(schema);
const A = '11111111-1111-1111-1111-111111111111';
const B = '22222222-2222-2222-2222-222222222222';

beforeAll(() => {
  execFileSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '-b'], {
    cwd: packageDir,
    stdio: 'pipe',
  });
}, 120_000);

afterAll(() => rmSync(directory, { recursive: true, force: true }));

function run(
  configuration: string,
  runId: string,
  allow = false,
): { events: EventEnvelope[]; stderr: string; status: number } {
  const env = { ...process.env };
  delete env.EVIDGATE_ALLOW_UNMAPPED;
  if (allow) env.EVIDGATE_ALLOW_UNMAPPED = '1';
  const run = spawnSync(process.execPath, [cli, 'test', '--config', configuration], {
    cwd: dirname(config),
    encoding: 'utf8',
    timeout: 90_000,
    env: {
      ...env,
      EVIDGATE_SPOOL_DIR: directory,
      EVIDGATE_RUN_ID: runId,
      EVIDGATE_TARGET_ENV: 'qa-local',
      EVIDGATE_E2E_OUTPUT_DIR: join(directory, `output-${runId}`),
    },
  });
  const expected = configuration === config || allow ? 0 : 1;
  expect(run.status, `${run.stdout}\n${run.stderr}`).toBe(expected);
  const spool = join(directory, runId, 'pw-e2e-0');
  const events = readdirSync(spool)
    .filter((file) => file.endsWith('.jsonl'))
    .sort()
    .flatMap((file) =>
      readFileSync(join(spool, file), 'utf8')
        .split('\n')
        .filter(Boolean)
        .map((line) => JSON.parse(line) as EventEnvelope),
    );
  for (const event of events) expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
  console.log(`QA real CLI ${runId}: exit ${String(run.status)}, ${events.length} valid envelopes`);
  return { events, stderr: run.stderr ?? '', status: run.status ?? -1 };
}

describe('independent reporter real CLI QA', () => {
  it('maps the shipped fixture with case/variant/target fields and schema-valid envelopes', () => {
    const { events, stderr } = run(config, 'qa-fixture');
    const finished = events.filter((event) => event.type === 'test.finished');
    expect(finished).toHaveLength(4);
    for (const event of finished) {
      expect(event.ext.case_id).toMatch(/^[a-f0-9-]{36}$/);
      expect(event.ext.variant_hash).toMatch(/^sha256:[a-f0-9]{64}$/);
      expect(event.ext.target_env).toBe('qa-local');
      expect(event.ext.identity_conflict).toBeUndefined();
      expect(event.execution_key).toBe(
        computeExecutionKey(
          String(event.ext.case_id),
          String(event.ext.variant_hash),
          String(event.ext.target_env),
        ),
      );
    }
    // C3: a fully mapped run trips no gate, so no `[evidgate]` line reaches stderr.
    expect(stderr).not.toContain('[evidgate]');
  });

  it('fails a conflicted real suite by default, allows the override, and retains identity across retries', () => {
    const tests = join(directory, 'tests');
    mkdirSync(tests);
    const api = pathToFileURL(join(root, 'node_modules/@playwright/test/index.mjs')).href;
    writeFileSync(
      join(tests, 'conflict.spec.mjs'),
      `import { test, expect } from ${JSON.stringify(api)};\n` +
        `test('conflict retry', { annotation: { type: 'evidgate.id', description: '${A}' } }, async ({}, info) => { expect(info.retry).toBe(1); });\n`,
    );
    writeFileSync(
      join(tests, 'evidgate.cases.yaml'),
      `version: 1\ncases:\n  - file: conflict.spec.mjs\n    evidgate.id: ${B}\n`,
    );
    const override = join(directory, 'playwright.config.mjs');
    writeFileSync(
      override,
      `import base from ${JSON.stringify(pathToFileURL(config).href)};\n` +
        `export default { ...base, reporter: [[${JSON.stringify(join(packageDir, 'dist/index.js'))}, { spool_dir: process.env.EVIDGATE_SPOOL_DIR, producer_id: 'pw-e2e-0' }]], testDir: ${JSON.stringify(tests)}, retries: 1 };\n`,
    );
    for (const allow of [false, true]) {
      const { events, stderr, status } = run(
        override,
        allow ? 'qa-conflict-allowed' : 'qa-conflict-default',
        allow,
      );
      const finished = events.filter((event) => event.type === 'test.finished');
      expect(finished.map((event) => event.attempt_no)).toEqual([1, 2]);
      for (const event of finished) {
        expect(event.ext.identity_conflict).toBe(true);
        expect(event.ext.case_id).toBeUndefined();
        expect(event.ext.variant_hash).toBe(finished[0]?.ext.variant_hash);
        expect(event.ext.target_env).toBe('qa-local');
      }
      const warnings = events
        .filter((event) => event.type === 'run.log' && event.ext.warn !== undefined)
        .map((event) => event.ext.warn as WarningPayload);
      expect(warnings.filter((warning) => warning.code === 'identity_conflict')).toHaveLength(1);
      expect(warnings.find((warning) => warning.code === 'unmapped')?.count).toBe(1);
      // C3: the gate writes one stderr line and overrides the passed run to failed; the
      // EVIDGATE_ALLOW_UNMAPPED=1 override keeps exit 0 and writes no line.
      if (allow) {
        expect(status).toBe(0);
        expect(stderr).not.toContain('[evidgate]');
      } else {
        expect(status).toBe(1);
        expect(stderr).toContain('[evidgate]');
        expect(stderr).toContain('EVIDGATE_ALLOW_UNMAPPED=1');
        expect(stderr).toContain(
          '(1 conflicted, 1 no case_id, 0 no variant_hash, 0 no target_env)',
        );
      }
    }
  }, 120_000);
});

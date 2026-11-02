#!/usr/bin/env node
// Supporting helper for scripts/dev/pilot0-dogfood.sh. Three local-only jobs, no new
// dependencies: build the run manifests and the one identity the reporter is told about,
// emit a throwaway Playwright config per run, and read one run's decision body.
//
// The execution key is the byte layout of
// src/Modules/Evidgate.Modules.Orchestrator/Domain/ExecutionKey.cs (four-byte big-endian
// length + UTF-8 per field, in case_id, variant_hash, target_env order). The variant hash
// is not re-implemented: the adapter's own port of the server normaliser
// (packages/adapter-playwright/src/variant.ts, `normalizeVariant`) is imported from its
// built output, so the empty parameter set hashes once, the same way the server does
// (fixtures/variant-hash/vectors.json, "empty-object"). The server still owns the real
// key; this copy only tells the reporter which planned execution it is producing.
import { createHash, randomUUID } from 'node:crypto';
import {
  mkdirSync,
  readdirSync,
  readFileSync,
  renameSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const TARGET_ENV = /^[a-z0-9][a-z0-9_-]{0,63}$/;
const PRODUCER = 'shard-1';
// The red run's only test maps to the same planned execution through the injected identity
// and fails on purpose, so reconcile has a real failed attempt to weigh.
const FORCED_FAILURE_SPEC = [
  "import { expect, test } from '@playwright/test';",
  '',
  "test('pilot0 forced failure', () => {",
  '  expect(1).toBe(2);',
  '});',
  '',
].join('\n');

function fail(message) {
  process.stderr.write(`pilot0-dogfood: ${message}\n`);
  process.exit(2);
}

function writeJson(path, value) {
  writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`, { mode: 0o600 });
}

// A spool chunk is drained by another process, so its new bytes replace the old file in one
// rename; a truncated or half-written chunk would fail the next `events send`.
function writeAtomically(path, text) {
  const temp = join(dirname(path), `.${basename(path)}.${process.pid}.tmp`);
  writeFileSync(temp, text, { mode: 0o600 });
  try {
    renameSync(temp, path);
  } catch (error) {
    try {
      unlinkSync(temp);
    } catch {
      // The rename already failed; the temp file is all that is left to remove.
    }
    throw error;
  }
}

function flags(argv) {
  const values = {};
  for (let i = 0; i < argv.length; i += 2) {
    const name = argv[i];
    const value = argv[i + 1];
    if (typeof name !== 'string' || !name.startsWith('--') || value === undefined) {
      fail(`bad argument list near ${String(name)}`);
    }
    values[name.slice(2)] = value;
  }
  return values;
}

function required(values, name) {
  const value = values[name];
  if (typeof value !== 'string' || value.length === 0) {
    fail(`missing --${name}`);
  }
  return value;
}

function uuid(values, name) {
  const value = required(values, name);
  if (!UUID.test(value)) {
    fail(`--${name} must be a lowercase UUID`);
  }
  return value;
}

function targetEnv(values, name) {
  const value = required(values, name);
  if (!TARGET_ENV.test(value)) {
    fail(`--${name} must match ^[a-z0-9][a-z0-9_-]{0,63}$`);
  }
  return value;
}

/** The second target env of the two-shard manifest: one shard per planned execution. */
function secondTargetEnv(env) {
  const suffix = '-2';
  const candidate = `${env.slice(0, 64 - suffix.length)}${suffix}`;
  if (candidate === env || !TARGET_ENV.test(candidate)) {
    fail('--target-env cannot be widened to a second target env (64 character limit)');
  }
  return candidate;
}

/** One length-prefixed UTF-8 field of the key (ExecutionKey.Write). */
function field(text) {
  const bytes = Buffer.from(text, 'utf8');
  const length = Buffer.alloc(4);
  length.writeUInt32BE(bytes.length);
  return Buffer.concat([length, bytes]);
}

function executionKey(caseId, variantHash, env) {
  return createHash('sha256')
    .update(Buffer.concat([field(caseId), field(variantHash), field(env)]))
    .digest('hex');
}

async function variantHashOfEmptyParams(adapterDist) {
  if (typeof adapterDist !== 'string' || adapterDist.length === 0) {
    fail('missing --adapter-dist');
  }
  const module = await import(pathToFileURL(join(adapterDist, 'variant.js')).href);
  const result = module.normalizeVariant({});
  if (!result.ok) {
    fail(`the adapter refused the empty variant: ${JSON.stringify(result.problems)}`);
  }
  return result.hash;
}

function plannedExecution(caseRevisionId, env) {
  return { case_revision_id: caseRevisionId, variant: { params: {} }, target_env: env };
}

function manifest({ suiteRevisionId, environmentRevisionId, caseRevisionId, envs }) {
  return {
    schema_version: 1,
    suite_revision_id: suiteRevisionId,
    environment_revision_id: environmentRevisionId,
    planned_executions: envs.map((env) => plannedExecution(caseRevisionId, env)),
    matrix: {
      case_revision_ids: [caseRevisionId],
      variants: [{ params: {} }],
      target_envs: envs,
    },
    shard_plan: {
      shards: envs.map((_, index) => ({
        shard_id: `shard-${index + 1}`,
        from_index: index,
        to_index: index + 1,
      })),
    },
    env_needs: { target_envs: envs.map((env) => ({ target_env: env })) },
  };
}

function writeManifests(work, { identity, single, twoShard }) {
  const paths = {
    identity: join(work, 'identity.json'),
    single: join(work, 'manifest-1.json'),
    twoShard: join(work, 'manifest-2.json'),
  };
  writeJson(paths.identity, identity);
  writeJson(paths.single, single);
  writeJson(paths.twoShard, twoShard);
  writeFileSync(join(work, 'red', 'pilot0-forced-failure.spec.ts'), FORCED_FAILURE_SPEC, {
    mode: 0o600,
  });
  return paths;
}

function prepare(values) {
  const work = required(values, 'work');
  const caseId = uuid(values, 'case-id');
  const caseRevisionId = uuid(values, 'case-revision-id');
  const suiteRevisionId = uuid(values, 'suite-revision-id');
  const environmentRevisionId = uuid(values, 'environment-revision-id');
  const env = targetEnv(values, 'target-env');
  const adapterDist = required(values, 'adapter-dist');
  const app = required(values, 'app');

  mkdirSync(join(work, 'red'), { recursive: true });
  return variantHashOfEmptyParams(adapterDist).then((variantHash) => {
    const identity = {
      execution_key: executionKey(caseId, variantHash, env),
      case_id: caseId,
      variant_hash: variantHash,
      target_env: env,
      source: 'annotation',
    };
    const paths = writeManifests(work, {
      identity,
      single: manifest({ suiteRevisionId, environmentRevisionId, caseRevisionId, envs: [env] }),
      twoShard: manifest({
        suiteRevisionId,
        environmentRevisionId,
        caseRevisionId,
        envs: [env, secondTargetEnv(env)],
      }),
    });
    process.stdout.write(
      `${JSON.stringify({
        execution_key: identity.execution_key,
        variant_hash: variantHash,
        identity: paths.identity,
        manifest_1: paths.single,
        manifest_2: paths.twoShard,
        app,
      })}\n`,
    );
  });
}

function configInputs(values) {
  const work = required(values, 'work');
  const mode = required(values, 'mode');
  if (mode !== 'green' && mode !== 'red') {
    fail('--mode must be green or red');
  }
  const runId = required(values, 'run-id');
  const label = required(values, 'label');
  const spool = required(values, 'spool');
  const output = required(values, 'output');
  const port = required(values, 'port');
  const app = required(values, 'app');
  if (!/^[A-Za-z0-9_-]{1,16}$/.test(label)) {
    fail('--label must be 1-16 letters, digits, _ or -');
  }
  if (!/^[0-9]{1,5}$/.test(port)) {
    fail('--port must be digits');
  }
  return { work, mode, runId, label, spool, output, port, app };
}

/** Writes one throwaway Playwright config that wraps the apps/web e2e workspace. */
function config(values) {
  const { work, mode, runId, label, spool, output, port, app } = configInputs(values);
  const identity = join(work, 'identity.json');
  const testDir = mode === 'green' ? join(app, 'e2e') : join(work, 'red');
  const testMatch = mode === 'green' ? 'smoke.spec.ts' : 'pilot0-forced-failure.spec.ts';
  const grep =
    mode === 'green'
      ? '  grep: /boots the built Angular bundle at the default locale \\(vi\\)/,\n'
      : '';
  const lines = [
    '// Generated by scripts/dev/pilot0-dogfood.sh for one run; deleted with its work dir.',
    "import { readFileSync } from 'node:fs';",
    "import { defineConfig } from '@playwright/test';",
    '',
    `const identity = JSON.parse(readFileSync(${JSON.stringify(identity)}, 'utf8'));`,
    `const baseURL = 'http://127.0.0.1:${port}';`,
    '',
    'export default defineConfig({',
    `  testDir: ${JSON.stringify(testDir)},`,
    `  testMatch: ${JSON.stringify(testMatch)},`,
    grep + '  retries: 0,',
    '  workers: 1,',
    '  fullyParallel: false,',
    `  outputDir: ${JSON.stringify(output)},`,
    '  reporter: [',
    "    ['list'],",
    '    [',
    "      '@evidgate/playwright-reporter',",
    '      {',
    `        spool_dir: ${JSON.stringify(spool)},`,
    `        run_id: ${JSON.stringify(runId)},`,
    `        producer_id: ${JSON.stringify(PRODUCER)},`,
    '        identity: () => identity,',
    '      },',
    '    ],',
    '  ],',
    "  use: { baseURL, trace: 'off', screenshot: 'off', video: 'off' },",
    "  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],",
    '  webServer: {',
    `    command: ${JSON.stringify(`node ${join(app, 'e2e', 'static-server.mjs')}`)},`,
    `    cwd: ${JSON.stringify(app)},`,
    '    url: `${baseURL}/`,',
    '    reuseExistingServer: false,',
    '    timeout: 30_000,',
    '  },',
    '});',
    '',
  ];
  const path = join(work, `playwright-${mode}-${label}.ts`);
  writeFileSync(path, lines.join('\n'), { mode: 0o600 });
  process.stdout.write(`${path}\n`);
}

/** Reads back one field, or the whole body, of GET /v1/runs/{r}/decision. */
async function decision(values) {
  const run = required(values, 'run');
  if (!UUID.test(run)) {
    fail('--run must be a lowercase UUID');
  }
  const apiUrl = (process.env.PILOT0_API_URL ?? '').replace(/\/$/, '');
  const devToken = process.env.PILOT0_DEV_TOKEN ?? '';
  if (apiUrl.length === 0 || devToken.length === 0) {
    fail('PILOT0_API_URL and PILOT0_DEV_TOKEN are required for the decision read');
  }
  const url = new URL(apiUrl);
  if (!['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || url.protocol !== 'http:') {
    fail('the decision read refuses a non-loopback http API URL');
  }
  const exchange = await fetch(`${apiUrl}/v1/ci/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ ingest_token: devToken }),
    signal: AbortSignal.timeout(30_000),
  });
  if (!exchange.ok) {
    fail(`the dev token exchange answered HTTP ${exchange.status}`);
  }
  let minted;
  try {
    minted = await exchange.json();
  } catch {
    fail('the dev token exchange answered malformed JSON');
  }
  if (typeof minted.token !== 'string' || minted.token.length === 0) {
    fail('the dev token exchange answered no token');
  }
  const response = await fetch(`${apiUrl}/v1/runs/${run}/decision`, {
    headers: { authorization: `Bearer ${minted.token}` },
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) {
    fail(`GET /v1/runs/${run}/decision answered HTTP ${response.status}`);
  }
  let body;
  try {
    body = await response.json();
  } catch {
    fail(`GET /v1/runs/${run}/decision answered malformed JSON`);
  }
  const pick = (name) => (name in body ? body[name] : null);
  process.stdout.write(
    `${JSON.stringify({
      status: pick('status'),
      decision: pick('decision'),
      completeness: pick('completeness'),
      outcome: pick('outcome'),
      missing: pick('missing'),
    })}\n`,
  );
}

/** Prints one member of a JSON object read from stdin (CLI --json lines). */
function parse(values) {
  const name = required(values, 'field');
  let text = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk) => {
    text += chunk;
  });
  process.stdin.on('end', () => {
    let parsed;
    try {
      parsed = JSON.parse(text);
    } catch {
      fail(`stdin was not one JSON value (looking for ${name})`);
    }
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      fail('stdin was not one JSON object');
    }
    const value = parsed[name];
    if (value === undefined || value === null) {
      return;
    }
    process.stdout.write(typeof value === 'string' ? `${value}\n` : `${JSON.stringify(value)}\n`);
  });
}

// The reporter stamps its run-level `run.log` markers with an all-zero placeholder key and the
// server accepts only planned keys (a gap the adapter's plan row owns). Until then the spool
// is rewritten line by line before `events send`; nothing else in an envelope changes.
function rekey(values) {
  const dir = required(values, 'dir');
  const placeholder = '0'.repeat(64);
  const key = required(values, 'key');
  let changed = 0;
  for (const name of readdirSync(dir).filter((file) => file.endsWith('.jsonl'))) {
    const path = join(dir, name);
    const original = readFileSync(path, 'utf8');
    const out = [];
    for (const [index, line] of original.split(/\r?\n/).entries()) {
      if (line.trim() === '') continue;
      let envelope;
      try {
        envelope = JSON.parse(line);
      } catch {
        fail(`spool line ${index + 1} of ${name} is not JSON`);
      }
      if (typeof envelope !== 'object' || envelope === null) {
        fail(`spool line ${index + 1} of ${name} is not a JSON object`);
      }
      if (envelope.execution_key !== placeholder) {
        out.push(line);
        continue;
      }
      envelope.execution_key = key;
      changed += 1;
      out.push(JSON.stringify(envelope));
    }
    const body = out.length === 0 ? '' : `${out.join('\n')}${original.endsWith('\n') ? '\n' : ''}`;
    writeAtomically(path, body);
  }
  process.stdout.write(`${changed}\n`);
}

const [command, ...rest] = process.argv.slice(2);
const values = flags(rest);
try {
  if (command === 'prepare') {
    await prepare(values);
  } else if (command === 'config') {
    config(values);
  } else if (command === 'decision') {
    await decision(values);
  } else if (command === 'parse') {
    parse(values);
  } else if (command === 'rekey') {
    rekey(values);
  } else if (command === 'invocation') {
    // One id per script invocation, so `runs create --idempotency-key` never replays an
    // earlier run's 201 (contracts/cli.md §9: the key, not the body, is what replays).
    process.stdout.write(`${randomUUID()}\n`);
  } else {
    fail(`unknown command ${String(command)}`);
  }
} catch (error) {
  // A parser message can quote the input, so only the stable error code is ever echoed.
  const code =
    error && typeof error === 'object' && typeof error.code === 'string' ? error.code : '';
  fail(code.length === 0 ? 'unexpected failure' : `unexpected failure (${code})`);
}

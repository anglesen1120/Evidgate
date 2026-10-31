// The acceptance case for the shipped schemas: build the package, copy its output somewhere
// outside this repository (where no `contracts/` directory exists above it) and run the real
// binary. Before the schemas were embedded, the validator searched upward for `contracts/`
// and every run from an install failed with `cannot locate …` — these specs fail the same
// way, so they pin the packaging, not just the unit.
import { execFileSync, spawnSync } from 'node:child_process';
import { cp, mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterAll, beforeAll, describe, expect, it } from 'vitest';

const packageDirectory = join(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = join(packageDirectory, '../..');
/** The committed build product this package's `build` script regenerates. */
const generatedFile = join(packageDirectory, 'src', 'contracts.generated.ts');
/** The generator the `build` script runs before `tsc -b`. */
const generatorCommand = 'node scripts/embed-contracts.mjs';

/** The run every staged invocation spools for (openapi `RunId`, `format: uuid`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The producer the fixture line belongs to. */
const producer = 'pw-shard-0';
/** A loopback API URL, which the dev-token path requires. */
const apiUrl = 'http://127.0.0.1:8080';
/** A dev token, assembled so no token-looking literal is stored. */
const devToken = ['dev', 'token', 'packaged'].join('-');

/** One schema-valid 00 §9.4 envelope, as the reporter writes it. */
function validEnvelope(): Record<string, unknown> {
  return {
    schema_version: 1,
    event_id: '00000000-0000-4000-8000-000000000000',
    producer_id: producer,
    sequence: 0,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  };
}

let stagingRoot = '';
let appDirectory = '';

/** The whole staged invocation is a local intake; a subprocess that outlives this is stuck. */
const stagedRunTimeoutMs = 60_000;

/**
 * Runs the staged binary for one `events send` intake. `EVIDGATE_SPOOL_MAX_BYTES=1` makes
 * the cap refuse the intake right after the envelope verdict, so the request path is never
 * reached and the outcome names which verdict the packaged schemas produced. The cap is the
 * default, not a per-case option: if the validator ever accepted a line it should refuse,
 * the run must stop at the cap instead of dialling the loopback API for the 30-minute send
 * deadline. The subprocess deadline covers the rest — a synchronous child cannot be
 * interrupted by Vitest.
 */
function runStagedBin(extraEnv: Record<string, string> = {}): {
  readonly status: number | null;
  readonly stderr: string;
} {
  const bin = join(appDirectory, 'dist', 'bin.js');
  const from = join(stagingRoot, 'input.jsonl');
  const child = spawnSync(
    process.execPath,
    [bin, 'events', 'send', '--run', runId, '--from', from],
    {
      encoding: 'utf8',
      timeout: stagedRunTimeoutMs,
      env: {
        ...process.env,
        EVIDGATE_API_URL: apiUrl,
        EVIDGATE_PROJECT: 'packaged-spec',
        EVIDGATE_DEV_TOKEN: devToken,
        EVIDGATE_SPOOL_DIR: join(stagingRoot, 'spool'),
        EVIDGATE_CACHE_DIR: join(stagingRoot, 'cache'),
        EVIDGATE_SPOOL_MAX_BYTES: '1',
        ...extraEnv,
      },
    },
  );
  if (child.error !== undefined) {
    throw child.error;
  }
  return { status: child.status, stderr: child.stderr };
}

/** True when some directory above `start` holds a `contracts/` tree (the old search target). */
function contractsAbove(start: string): boolean {
  let level = start;
  for (;;) {
    if (existsSync(join(level, 'contracts'))) {
      return true;
    }
    const parent = dirname(level);
    if (parent === level) {
      return false;
    }
    level = parent;
  }
}

/** The `build` script text, so the steps run below can be pinned to it. */
let buildScript = '';
/** The generated module before and after the build's generator step ran. */
let generatedBefore = '';
let generatedAfter = '';

beforeAll(async () => {
  // The build script is the contract, but the generator must not be a hole in the drift
  // guard: running it silently would repair a stale checked-in module before the parity
  // spec ever reads it. So its output is captured on both sides of the step instead of
  // being trusted, and the step itself is asserted to be byte-identical.
  const manifest = JSON.parse(await readFile(join(packageDirectory, 'package.json'), 'utf8')) as {
    scripts?: Record<string, string>;
  };
  buildScript = manifest.scripts?.['build'] ?? '';

  generatedBefore = await readFile(generatedFile, 'utf8');
  execFileSync('npm', ['run', 'embed:contracts'], { cwd: packageDirectory, stdio: 'pipe' });
  execFileSync(
    process.execPath,
    [join(repositoryRoot, 'node_modules', 'typescript', 'bin', 'tsc'), '-b'],
    { cwd: packageDirectory, stdio: 'pipe' },
  );
  generatedAfter = await readFile(generatedFile, 'utf8');

  stagingRoot = await mkdtemp(join(tmpdir(), 'evidgate-packaged-'));
  appDirectory = join(stagingRoot, 'app');
  await cp(join(packageDirectory, 'dist'), join(appDirectory, 'dist'), { recursive: true });
  await writeFile(
    join(appDirectory, 'package.json'),
    `${JSON.stringify({ name: 'evidgate-packaged-spec', version: '0.0.0', type: 'module' }, null, 2)}\n`,
    'utf8',
  );
  // Bare specifiers (`ajv`, `@evidgate/sdk`, …) resolve through the workspace install; the
  // staged copy is what the process runs, and nothing of it points back at the checkout.
  await symlink(join(repositoryRoot, 'node_modules'), join(appDirectory, 'node_modules'), 'dir');
}, 180_000);

afterAll(async () => {
  if (stagingRoot !== '') {
    await rm(stagingRoot, { recursive: true, force: true });
  }
});

describe('evidgate package build', () => {
  it('runs the schema generator before the TypeScript build', () => {
    expect(buildScript).toContain(generatorCommand);
    expect(buildScript.indexOf(generatorCommand)).toBeLessThan(buildScript.indexOf('tsc -b'));
  });

  it('leaves the committed schema module byte-identical when it regenerates it', () => {
    expect(generatedBefore.length).toBeGreaterThan(0);
    expect(generatedAfter).toBe(generatedBefore);
  });
});

describe('evidgate bin copied out of the repository', () => {
  it('has no contracts directory anywhere above the staged install', () => {
    expect(existsSync(join(appDirectory, 'dist', 'bin.js'))).toBe(true);
    expect(contractsAbove(appDirectory)).toBe(false);
  });

  it('refuses an extra envelope member with the schema verdict, not a missing contracts tree', async () => {
    await writeFile(
      join(stagingRoot, 'input.jsonl'),
      `${JSON.stringify({ ...validEnvelope(), unexpected_member: true })}\n`,
      'utf8',
    );
    const { status, stderr } = runStagedBin();

    expect(status).toBe(3);
    expect(stderr).toContain('error envelope_invalid');
    expect(stderr).toContain('additional_properties');
    expect(stderr).not.toContain('cannot locate');
  });

  it('validates a good envelope through to the spool cap check', async () => {
    await writeFile(
      join(stagingRoot, 'input.jsonl'),
      `${JSON.stringify(validEnvelope())}\n`,
      'utf8',
    );
    const { status, stderr } = runStagedBin();

    expect(status).toBe(3);
    expect(stderr).toContain('error spool_cap_exceeded');
    expect(stderr).not.toContain('envelope_invalid');
    expect(stderr).not.toContain('cannot locate');
  });
});

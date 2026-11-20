// The fixtures the `manifests build` specs share: the plan page and list-report builders, a
// temp root per test, a cached bearer so no spec drives the exchange, and a runner that fails
// the test if the bearer ever reaches stdout, stderr or a thrown message.
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { SuiteRevisionExecutionPlan, SuiteRevisionExecutionPlanMember } from '@evidgate/sdk';
import { afterEach, expect } from 'vitest';

import { writeCachedToken } from '../auth/tokenCache.js';
import { canonicalHash } from '../canonical.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';

import {
  capture,
  jsonResponse,
  stubFetch,
  type Captured,
  type StubCall,
  type StubHandler,
  type StubHarness,
} from './cliSpec.testing.js';

export { capture, jsonResponse, stubFetch };
export type { Captured, StubCall };

/** The suite revision every fixture builds for. */
export const revision = '11111111-1111-4111-8111-111111111111';
/** The bearer a cached token carries; no failure path may ever print it. */
export const bearer = 'egt_build_bearer_secret';
/** A project id in a list report. */
const projectId = 'p';
/** The one target environment a built manifest declares. */
export const targetEnv = 'default';

/** The `n`th plan member: distinct case and revision, `{}` variant unless `params` is given. */
export function member(
  n = 1,
  params: Record<string, unknown> = {},
): SuiteRevisionExecutionPlanMember {
  return {
    case_id: `22222222-2222-4222-8222-${String(n).padStart(12, '0')}`,
    case_revision_id: `33333333-3333-4333-8333-${String(n).padStart(12, '0')}`,
    variant_hash: canonicalHash(params),
  };
}

/** One contract-shaped page (`SuiteRevisionExecutionPlan`). */
export function page(
  members: readonly SuiteRevisionExecutionPlanMember[] = [member()],
  next_cursor: string | null = null,
): SuiteRevisionExecutionPlan {
  return { suite_revision_id: revision, members: [...members], next_cursor };
}

/** A Playwright list report enumerating one test per member, in member order. */
export function report(
  members: readonly SuiteRevisionExecutionPlanMember[] = [member()],
  params: Record<string, unknown> = {},
): string {
  return JSON.stringify({
    config: { projects: [{ id: projectId, name: 'chromium', metadata: { evidgate: { params } } }] },
    suites: [
      {
        file: 'tests/a.spec.ts',
        specs: members.map((m, index) => ({
          title: `case-${index}`,
          tests: [
            {
              projectId,
              annotations: [{ type: 'evidgate.id', description: m.case_id }],
            },
          ],
        })),
      },
    ],
    errors: [],
  });
}

export interface BuildFixture {
  readonly root: string;
  readonly list: string;
  readonly out: string;
  readonly env: Record<string, string>;
  readonly args: string[];
}

export interface RunOutcome extends Captured {
  readonly code: number;
}

export interface BuildFixtureOptions {
  readonly text?: string;
  readonly cached?: boolean;
  readonly apiUrl?: string;
}

type Setup = (options?: BuildFixtureOptions) => Promise<BuildFixture>;
type Run = (
  fixture: BuildFixture,
  fetchImpl: typeof fetch,
  args?: readonly string[],
) => Promise<RunOutcome>;
type Refusal = (
  code: string,
  handler: StubHandler,
  fixture?: BuildFixture,
) => Promise<{ fixture: BuildFixture; stub: StubHarness; outcome: RunOutcome }>;

/**
 * Registers the temp roots, and hands back the fixture factory and the runner. `run` asserts on
 * every path that the bearer never leaves the process, then returns the captured streams.
 */
export function useBuildSpec(prefix = 'evidgate-build-') {
  const dirs: string[] = [];
  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });
  const setup = setupFixture(prefix, dirs);
  const run = runBuild();
  return {
    setup,
    run,
    refusal: refuseBuild(setup, run),
    tempDir: () => mkdtemp(join(tmpdir(), prefix)),
  };
}

/** A fixture with a written list report, a free `--out` path and a cached bearer by default. */
function setupFixture(prefix: string, dirs: string[]): Setup {
  return async (options = {}) => {
    const root = await mkdtemp(join(tmpdir(), prefix));
    dirs.push(root);
    const list = join(root, 'list.json');
    await writeFile(list, options.text ?? report());
    const env: Record<string, string> = {
      EVIDGATE_API_URL: options.apiUrl ?? 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_build_source',
      EVIDGATE_CACHE_DIR: root,
    };
    if (options.cached !== false) {
      await writeCachedToken(resolveConfig({ env }), {
        token: bearer,
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      });
    }
    const out = join(root, 'manifest.json');
    return {
      root,
      list,
      out,
      env,
      args: [
        'manifests',
        'build',
        '--suite-revision',
        revision,
        '--playwright-list',
        list,
        '--out',
        out,
      ],
    };
  };
}

/** Runs the command, and fails the test if the bearer reaches any stream or a thrown message. */
function runBuild(): Run {
  return async (fixture, fetchImpl, args = fixture.args) => {
    const captured = capture();
    let thrown = '';
    let code = -1;
    try {
      code = await main([...args], fixture.env, captured.io, {
        fetch: fetchImpl,
        sleep: () => Promise.resolve(),
      });
    } catch (error) {
      thrown = error instanceof Error ? error.message : String(error);
      throw error;
    } finally {
      const streams = `${captured.stdout.join('')}${captured.stderr.join('')}${thrown}`;
      expect(streams).not.toContain(bearer);
    }
    return { ...captured, code };
  };
}

/**
 * Runs a refusal: the answering page(s) come from `handler`, and the result must exit 3 with
 * `code` and print nothing to stdout.
 */
function refuseBuild(setup: Setup, run: Run): Refusal {
  return async (code, handler, fixture) => {
    const resolved = fixture ?? (await setup());
    const stub = stubFetch(handler);
    const outcome = await run(resolved, stub.fetchImpl);
    expect(outcome.code).toBe(3);
    expect(outcome.stderr.join('')).toContain(`evidgate: error ${code}: `);
    expect(outcome.stdout).toEqual([]);
    return { fixture: resolved, stub, outcome };
  };
}

// 04.3.1-T1.1/T1.2: the reporter Playwright loads from
// `reporter: [['@evidgate/playwright-reporter', { spool_dir, run_id, producer_id }]]`.
// It implements the four documented callbacks (onBegin/onTestBegin/onTestEnd/onEnd) and
// emits one 00 §9.4 envelope per callback into the spool; it never opens a socket
// (04 §7). Construction is fail-fast: missing/invalid options, an unsupported engine
// (AC5) and a spool initialisation failure (mkdir/open) all throw before any callback.
// After construction the callbacks never throw — a spool write failure is buffered and
// re-thrown from onEnd so the Playwright run reports the reporter failure instead of
// losing evidence silently (task file §5).
import type {
  FullResult,
  Reporter,
  TestCase,
  TestResult,
  TestStep,
} from '@playwright/test/reporter';

import { buildCapabilityDocument } from './capability.js';
import {
  buildEnvelope,
  EXT_SCHEMA,
  type EventType,
  type ExtPayload,
  type UnsignedEnvelope,
} from './envelope.js';
import { filterExt } from './extFilter.js';
import { PLACEHOLDER_EXECUTION_KEY, type IdentityResolver } from './identity.js';
import { SpoolWriter } from './spool.js';
import { assertSupportedEngine, readInstalledPlaywrightVersion } from './versionGuard.js';

export interface EvidgateReporterOptions {
  /** Spool root; falls back to `EVIDGATE_SPOOL_DIR`. */
  spool_dir?: string;
  /** Run id; falls back to `EVIDGATE_RUN_ID`. */
  run_id?: string;
  /**
   * Producer id. Required: no env fallback exists — `EVIDGATE_PRODUCER_ID` is only
   * floated in `plans/open-items/02-catalog-ingest-runs-evidence.md` (OI-04-02, for
   * 04.3.3) and is not defined in `contracts/` or the plans, so the reporter does not
   * read it.
   */
  producer_id?: string;
  /** Emit `test.step` envelopes for `result.steps`. Default true (AC6). */
  steps?: boolean;
  /** Capability `mode`. Default `full`. */
  mode?: 'full' | 'ingest_only';
  /** Execution-identity resolver; defaults to the placeholder (04.3.2-T1 hands over). */
  identity?: IdentityResolver;
  /** Test-only seam: overrides the detected @playwright/test version for the guard. */
  engineVersion?: string;
}

interface EnvelopeDraft {
  type: EventType;
  attempt_no: number;
  execution_key: string;
  ext: ExtPayload;
  status?: string;
  duration_ms?: number;
}

export class EvidgateReporter implements Reporter {
  private readonly producerId: string;
  private readonly stepsEnabled: boolean;
  private readonly mode: 'full' | 'ingest_only';
  private readonly identity: IdentityResolver | undefined;
  private readonly engineVersion: string;
  private readonly writer: SpoolWriter;
  private bufferedError: Error | null = null;
  private secretKeysDroppedCount = 0;

  constructor(options: EvidgateReporterOptions = {}) {
    const spoolDir = options.spool_dir ?? process.env.EVIDGATE_SPOOL_DIR;
    const runId = options.run_id ?? process.env.EVIDGATE_RUN_ID;
    const producerId = options.producer_id;
    if (spoolDir === undefined || spoolDir.length === 0) {
      throw new Error(
        '@evidgate/playwright-reporter: the spool_dir option or EVIDGATE_SPOOL_DIR is required',
      );
    }
    if (runId === undefined || runId.length === 0) {
      throw new Error(
        '@evidgate/playwright-reporter: the run_id option or EVIDGATE_RUN_ID is required',
      );
    }
    if (producerId === undefined || producerId.length === 0) {
      throw new Error(
        '@evidgate/playwright-reporter: the producer_id option is required (no env fallback: ' +
          'EVIDGATE_PRODUCER_ID is not defined in contracts/ or the plans)',
      );
    }
    const engineVersion = options.engineVersion ?? readInstalledPlaywrightVersion();
    // AC5: the guard runs before the spool writer is created, so a rejected engine
    // leaves no partial emit behind.
    assertSupportedEngine(engineVersion);
    this.engineVersion = engineVersion;
    this.producerId = producerId;
    this.stepsEnabled = options.steps ?? true;
    this.mode = options.mode ?? 'full';
    this.identity = options.identity;
    this.writer = new SpoolWriter({ spoolDir, runId, producerId });
  }

  /** How many secret-looking `ext` keys were dropped (04 §6, S3). */
  get secretKeysDropped(): number {
    return this.secretKeysDroppedCount;
  }

  /**
   * Playwright adds its default console reporter only when no configured reporter
   * prints to stdio (`reporters.some(r => r.printsToStdio ? r.printsToStdio() : true)`,
   * node_modules/playwright/lib/runner/index.js). This reporter writes to the spool, not
   * stdout, so returning false keeps the line/dot reporter and the run summary visible
   * (AC1 usability: a run with only this reporter must not be silent).
   */
  printsToStdio(): boolean {
    return false;
  }

  /** Sequence 1 is the capability document, sequence 2 the `run_begin` marker (AC3). */
  onBegin(): void {
    this.guard(() => {
      const capability = buildCapabilityDocument({
        engineVersion: this.engineVersion,
        stepEvents: this.stepsEnabled,
        mode: this.mode,
      });
      this.writer.appendMany([
        this.draft({
          type: 'run.log',
          attempt_no: 1,
          execution_key: PLACEHOLDER_EXECUTION_KEY,
          ext: { schema: EXT_SCHEMA, capability },
        }),
        this.draft({
          type: 'run.log',
          attempt_no: 1,
          execution_key: PLACEHOLDER_EXECUTION_KEY,
          ext: { schema: EXT_SCHEMA, marker: 'run_begin' },
        }),
      ]);
      // CR2: one durable batch per callback, so a killed run loses at most the current
      // callback's envelopes instead of the whole suite (task §5/§6). 64 stays the
      // upper bound on how many envelopes a single flush carries.
      this.writer.flush();
    });
  }

  onTestBegin(test: TestCase, result: TestResult): void {
    this.guard(() => {
      this.writer.append(
        this.draft({
          type: 'test.started',
          attempt_no: result.retry + 1,
          execution_key: this.executionKey(test),
          ext: this.testExt(test, result),
        }),
      );
    });
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    this.guard(() => {
      const attemptNo = result.retry + 1;
      const executionKey = this.executionKey(test);
      this.writer.append(
        this.draft({
          type: 'test.finished',
          attempt_no: attemptNo,
          execution_key: executionKey,
          status: result.status,
          ext: this.testExt(test, result),
          ...durationField(result.duration),
        }),
      );
      if (this.stepsEnabled) {
        // PS6: walk the step tree lazily, appending per step, so the working set stays
        // at the flush threshold instead of materialising a step array and a draft array.
        for (const step of walkSteps(result.steps)) {
          this.writer.append(
            this.draft({
              type: 'test.step',
              attempt_no: attemptNo,
              execution_key: executionKey,
              ext: this.stepExt(step),
              ...durationField(step.duration),
            }),
          );
        }
      }
      // CR2: one durable batch per test.
      this.writer.flush();
    });
  }

  onEnd(result: FullResult): void {
    this.guard(() => {
      this.writer.append(
        this.draft({
          type: 'run.log',
          attempt_no: 1,
          execution_key: PLACEHOLDER_EXECUTION_KEY,
          status: result.status,
          ext: { schema: EXT_SCHEMA, marker: 'run_end' },
        }),
      );
    });
    let closeError: Error | null = null;
    try {
      this.writer.close();
    } catch (error) {
      closeError = toError(error);
    }
    const failure = this.bufferedError ?? closeError;
    if (failure !== null) throw failure;
  }

  private draft(input: EnvelopeDraft): UnsignedEnvelope {
    return buildEnvelope({ producer_id: this.producerId, ...input });
  }

  private executionKey(test: TestCase): string {
    return this.identity === undefined
      ? PLACEHOLDER_EXECUTION_KEY
      : this.identity(test).execution_key;
  }

  private testExt(test: TestCase, result: TestResult): ExtPayload {
    return this.applyExtFilter({
      ...ownMembers(test),
      ...ownMembers(result),
      schema: EXT_SCHEMA,
      file: test.location.file,
      project: test.parent.project()?.name,
      engine: this.engineVersion,
    });
  }

  private stepExt(step: TestStep): ExtPayload {
    const candidate: Record<string, unknown> = {
      ...ownMembers(step),
      schema: EXT_SCHEMA,
    };
    // PS1: Playwright's own `pw:api`/`expect` step titles embed call parameters — e.g.
    // `Fill "hunter2"` or a `goto` URL with its query string — so `title` is forwarded
    // only for user-authored categories. `category` and `location` still identify the step.
    if (!isUserAuthoredStepCategory(step.category)) delete candidate.title;
    return this.applyExtFilter(candidate);
  }

  private applyExtFilter(candidate: Record<string, unknown>): ExtPayload {
    const result = filterExt(candidate);
    this.secretKeysDroppedCount += result.secretDropped.length;
    return { schema: EXT_SCHEMA, ...result.ext };
  }

  private guard(run: () => void): void {
    try {
      run();
    } catch (error) {
      this.bufferedError ??= toError(error);
    }
  }
}

/**
 * Own enumerable members of a Playwright object as a plain record. The ext filter is
 * the boundary that decides which may reach `ext` (04 §6: "fields are allow-listed per
 * adapter; secret-looking keys are dropped at the boundary"). Playwright's classes carry
 * no index signature, so the spread is the seam that turns them into a plain record.
 */
function ownMembers(value: object): Record<string, unknown> {
  return { ...value };
}

/**
 * PS1: step categories whose `title` is authored by the test writer. Playwright's own
 * `pw:api` and `expect` titles embed call parameters (a filled value, a URL with its
 * query string), so only these categories may forward `title` into `ext`.
 */
const USER_AUTHORED_STEP_CATEGORIES: Record<string, true> = {
  'test.step': true,
  hook: true,
  fixture: true,
};

function isUserAuthoredStepCategory(category: unknown): boolean {
  return typeof category === 'string' && USER_AUTHORED_STEP_CATEGORIES[category] === true;
}

/**
 * PS2: the `duration_ms` member for an envelope, or `{}` when the value is not a
 * non-negative finite number. The envelope schema requires an integer >= 0, and
 * Playwright initialises an unfinished step's duration to -1; non-integers are rounded.
 */
function durationField(value: unknown): { duration_ms?: number } {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return {};
  return { duration_ms: Math.round(value) };
}

/**
 * PS6: depth-first (parent before child) walk of the nested `result.steps` tree,
 * yielding each step lazily so the caller appends per step instead of materialising the
 * whole tree. Playwright 1.63's `TestStep.steps` is a nested array
 * (node_modules/playwright/types/testReporter.d.ts), so a recursive walk is required to
 * emit every step; the decision is recorded in the package README.
 */
function* walkSteps(steps: readonly TestStep[]): Generator<TestStep> {
  for (const step of steps) {
    yield step;
    yield* walkSteps(step.steps);
  }
}

function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

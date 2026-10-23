// 04.3.1-T1.1/T1.2: the reporter Playwright loads from
// `reporter: [['@evidgate/playwright-reporter', { spool_dir, run_id, producer_id }]]`.
// It implements the four documented callbacks (onBegin/onTestBegin/onTestEnd/onEnd) and
// emits one 00 §9.4 envelope per callback into the spool; it never opens a socket
// (04 §7). Construction is fail-fast: missing/invalid options, an unsupported engine
// (AC5) and a spool initialisation failure (mkdir/open) all throw before any callback.
// After construction the callbacks never throw — a spool write failure is buffered and
// re-thrown from onEnd so the Playwright run reports the reporter failure instead of
// losing evidence silently (task file §5).
//
// 04.3.2-T1.2/T1.4: every test envelope carries the resolved identity — `ext.case_id`
// (annotation `evidgate.id`, else the sidecar manifest), `ext.variant_hash` (server
// `VariantNormalizer` semantics over the project's declared params) and
// `ext.target_env` — plus `ext.identity_conflict` when annotation and sidecar disagree.
// Warnings are data: `run.log` envelopes with `ext.warn`. `onEnd` returns
// `{ status: 'failed' }` when any test lacked a complete identity (no `case_id`, no
// `variant_hash`, no `target_env`, or a conflict), unless `EVIDGATE_ALLOW_UNMAPPED=1`;
// the gate also writes one `[evidgate]` line to stderr, naming the per-cause counts, so a
// CI log explains the failure, and it overrides the status only for a `passed` run (S3/C3).
//
// 04.3.2-T2.1/T2.2: each attempt is its own `test.finished` with `attempt_no = retry + 1`
// (AC5); the envelope `status` is the raw Playwright status and `ext.normalized` (plus
// `skip_reason`/`xfail`/`error_summary` when they apply) carries the 00 §8.3 mapping
// (AC6). An unknown raw status maps to `error` and emits one `unknown_status` warning.
//
// 04.3.3-T1: the producer identity is derived from Playwright's shard config (T54,
// `resolveProducerId`: `shard-<current>`, `shard-1` unsharded), so `producer_id` is no
// longer a required option; an explicit option still wins but must equal the derived id
// when Playwright selected a shard, or the run is refused before any envelope (two shards
// sharing one producer would interleave one sequence). Because the config is only known
// at `onBegin`, the spool writer is opened there — a spool initialisation failure is
// therefore buffered from `onBegin` and re-thrown from `onEnd` like every other callback
// failure, instead of at construction. In a `merge-reports` replay the reporter emits
// nothing: each event already reached the spool under its own shard's producer id and
// sequence, and the replay exposes no per-event shard identity (AC3).
import type {
  FullConfig,
  FullResult,
  Reporter,
  Suite,
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
import {
  createIdentityResolver,
  locationOf,
  PLACEHOLDER_EXECUTION_KEY,
  testKey,
  validTargetEnv,
  type Identity,
  type IdentityResolver,
  type VariantOutcome,
  type WarningPayload,
} from './identity.js';
import { loadSidecarIndex, type SidecarFs } from './sidecar.js';
import { isMergeReplay, isProducerId, resolveProducerId } from './shard.js';
import type { ResolvedProducerId } from './shard.js';
import { attemptOf, mapStatus, printable, type StatusMapping } from './status.js';
import { SpoolWriter } from './spool.js';
import type { SpoolShard, SpoolWriterOptions } from './spool.js';
import { assertSupportedEngine, readInstalledPlaywrightVersion } from './versionGuard.js';

/**
 * The most warning `run.log` envelopes one run may emit. A hostile suite (thousands of
 * conflicting annotations) must not be able to flood the spool; the overflow is
 * summarised by one `warnings_truncated` envelope at `onEnd`.
 */
export const MAX_WARNING_ENVELOPES = 100;

export interface EvidgateReporterOptions {
  /** Spool root; falls back to `EVIDGATE_SPOOL_DIR`. */
  spool_dir?: string;
  /** Run id; falls back to `EVIDGATE_RUN_ID`. */
  run_id?: string;
  /**
   * Producer id override. Optional since 04.3.3-T1: without it the id is derived from
   * Playwright's shard config at `onBegin` (`shard-<current>`, `shard-1` unsharded, T54).
   * An explicit value must match `^[A-Za-z0-9._-]{1,128}$` (one spool path segment) and,
   * when Playwright selected a shard, must equal the derived id — otherwise `onBegin`
   * refuses (see `resolveProducerId` and `contracts/adapter/playwright-mapping.md`).
   * There is still no env fallback: `EVIDGATE_PRODUCER_ID` is only floated in
   * `plans/open-items/02-catalog-ingest-runs-evidence.md` (OI-04-02) and is not defined in
   * `contracts/` or the plans, so the reporter does not read it.
   */
  producer_id?: string;
  /** Emit `test.step` envelopes for `result.steps`. Default true (AC6). */
  steps?: boolean;
  /** Capability `mode`. Default `full`. */
  mode?: 'full' | 'ingest_only';
  /** Execution-identity resolver; overrides the built-in 04.3.2-T1 mapping. */
  identity?: IdentityResolver;
  /** Test-only seam: injects the sidecar filesystem reader (04.3.2-T1.1). */
  sidecarFs?: SidecarFs;
  /** Test-only seam: overrides the detected @playwright/test version for the guard. */
  engineVersion?: string;
  /**
   * Test-only seam: the CLI arguments used to recognise a `merge-reports` replay
   * (`process.argv.slice(2)` by default, 04.3.3-T1).
   */
  argv?: readonly string[];
  /**
   * Test-only seam: the spool writer's single-writer lock (04.3.3-T1). The restart test
   * needs a writer whose recorded holder pid reads as dead, so a second reporter can take
   * over the stale lock without killing a real process.
   */
  lock?: SpoolWriterOptions['lock'];
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
  /** The explicit option, kept for the `onBegin` consistency check (04.3.3-T1). */
  private readonly explicitProducerId: string | undefined;
  /** Resolved at `onBegin`: the explicit option or the derived `shard-<index>` (T54). */
  private producerId: string | undefined;
  private readonly spoolDir: string;
  private readonly runId: string;
  private readonly writerLock: SpoolWriterOptions['lock'];
  /**
   * Opened at `onBegin`, because the shard — and therefore the producer id — is only known
   * from the resolved config there (04.3.3-T1). Undefined means "this reporter emits
   * nothing": a `merge-reports` replay, or an `onBegin` that was refused.
   */
  private writer: SpoolWriter | undefined;
  /** True in a `merge-reports` replay: every event was already ingested per shard (AC3). */
  private readonly mergeReplay: boolean;
  private mergeNoticeWritten = false;
  private readonly stepsEnabled: boolean;
  private readonly mode: 'full' | 'ingest_only';
  private readonly identity: IdentityResolver | undefined;
  private readonly sidecarFs: SidecarFs | undefined;
  private readonly engineVersion: string;
  /** `EVIDGATE_TARGET_ENV`, validated once in the ctor (no env read outside it). */
  private readonly targetEnv: string | undefined;
  private readonly targetEnvWarning: WarningPayload | undefined;
  private readonly allowUnmapped: boolean;
  private readonly variantCache = new WeakMap<object, VariantOutcome>();
  private readonly targetEnvWarned = new WeakSet<object>();
  private readonly seenTests = new Set<string>();
  private resolver: IdentityResolver | undefined;
  private unidentifiedCount = 0;
  private conflictCount = 0;
  private noCaseIdCount = 0;
  private noVariantHashCount = 0;
  private noTargetEnvCount = 0;
  private warningCount = 0;
  /** CR-2: raw statuses already reported by an `unknown_status` warning. */
  private readonly unknownStatusesWarned = new Set<string>();
  private suppressedWarnings = 0;
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
    // 04.3.3-T1: the explicit override is validated here so a bad id fails at construction,
    // exactly as before; the id itself is resolved at onBegin (the shard is in the config).
    if (producerId !== undefined && producerId.length > 0 && !isProducerId(producerId)) {
      throw new Error(
        `@evidgate/playwright-reporter: invalid producer_id ${JSON.stringify(producerId)}: ` +
          'must match ^[A-Za-z0-9._-]{1,128}$ (one safe spool path segment)',
      );
    }
    const engineVersion = options.engineVersion ?? readInstalledPlaywrightVersion();
    // AC5: the guard runs before the spool writer is created, so a rejected engine
    // leaves no partial emit behind.
    assertSupportedEngine(engineVersion);
    this.engineVersion = engineVersion;
    this.explicitProducerId =
      producerId !== undefined && producerId.length > 0 ? producerId : undefined;
    this.spoolDir = spoolDir;
    this.runId = runId;
    this.writerLock = options.lock;
    // AC3: a `merge-reports` replay is recognised from the CLI arguments, the only signal
    // available before any callback (shard.ts).
    this.mergeReplay = isMergeReplay(options.argv ?? process.argv.slice(2));
    this.stepsEnabled = options.steps ?? true;
    this.mode = options.mode ?? 'full';
    this.identity = options.identity;
    this.sidecarFs = options.sidecarFs;

    // 04.3.2-T1.4: the target env is read once here. An invalid value falls back to the
    // Playwright project name and is reported once at onBegin.
    const rawTargetEnv = process.env.EVIDGATE_TARGET_ENV;
    this.targetEnv = validTargetEnv(rawTargetEnv?.trim());
    this.targetEnvWarning =
      rawTargetEnv !== undefined && this.targetEnv === undefined
        ? {
            code: 'invalid_target_env',
            message:
              'EVIDGATE_TARGET_ENV does not match ^[a-z0-9][a-z0-9_-]{0,63}$; ' +
              'falling back to the Playwright project name',
          }
        : undefined;
    this.allowUnmapped = process.env.EVIDGATE_ALLOW_UNMAPPED === '1';
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

  /**
   * Sequence 1 is the capability document, sequence 2 the `run_begin` marker (AC3).
   * The sidecar index is built once here from the suite's test files (04.3.2 §4), pinned
   * to `config.rootDir` so every shard reads the same manifests (04.3.3-T1, #32); its
   * warnings and a pending `invalid_target_env` are emitted after the two markers.
   *
   * 04.3.3-T1 owns two more things here: the shard's producer identity (T54, with the
   * explicit-override consistency rule) and the opening of the spool writer, which needs
   * that identity. In a `merge-reports` replay nothing is emitted at all (AC3).
   */
  onBegin(config?: FullConfig, suite?: Suite): void {
    this.guard(() => {
      if (this.mergeReplay) {
        this.noteMergeReplay();
        return;
      }
      const producer = this.resolveProducer(config);
      this.producerId = producer.producer_id;
      this.writer = new SpoolWriter({
        spoolDir: this.spoolDir,
        runId: this.runId,
        producerId: producer.producer_id,
        ...(producer.shard === undefined ? {} : { shard: producer.shard }),
        ...(this.writerLock === undefined ? {} : { lock: this.writerLock }),
      });

      const testFiles = new Set<string>();
      for (const test of suite?.allTests() ?? []) testFiles.add(test.location.file);
      const index = loadSidecarIndex(
        testFiles,
        this.sidecarFs,
        config?.rootDir === undefined ? {} : { rootDir: config.rootDir },
      );
      // P3: warnings the sidecar loader discarded past its own cap still count as
      // suppressed, so `warnings_truncated` reports the full loss.
      this.suppressedWarnings += index.droppedWarnings;
      this.resolver = createIdentityResolver({
        sidecar: index,
        targetEnv: this.targetEnv,
        warn: (warning) => this.warn(warning),
        variantCache: this.variantCache,
        targetEnvWarned: this.targetEnvWarned,
      });

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

      for (const warning of index.warnings) {
        this.warn({ code: warning.code, message: warning.message, path: warning.path });
      }
      if (this.targetEnvWarning !== undefined) this.warn(this.targetEnvWarning);

      // CR2: one durable batch per callback, so a killed run loses at most the current
      // callback's envelopes instead of the whole suite (task §5/§6). 64 stays the
      // upper bound on how many envelopes a single flush carries.
      this.writer.flush();
    });
  }

  onTestBegin(test: TestCase, result: TestResult): void {
    this.guard(() => {
      const writer = this.activeWriter();
      if (writer === undefined) return;
      const identity = this.identityOf(test);
      this.noteIdentity(test, identity);
      writer.append(
        this.draft({
          type: 'test.started',
          attempt_no: attemptOf(result),
          execution_key: identity.execution_key,
          ext: this.testExt(test, result, identity),
        }),
      );
    });
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    this.guard(() => {
      const writer = this.activeWriter();
      if (writer === undefined) return;
      const attemptNo = attemptOf(result);
      const identity = this.identityOf(test);
      this.noteIdentity(test, identity);
      const mapping = mapStatus({
        status: result.status,
        expectedStatus: test.expectedStatus,
        annotations: [...(test.annotations ?? []), ...(result.annotations ?? [])],
        errors: result.errors,
      });
      writer.append(
        this.draft({
          type: 'test.finished',
          attempt_no: attemptNo,
          execution_key: identity.execution_key,
          status: mapping.raw,
          ext: this.testExt(test, result, identity, mapping),
          ...durationField(result.duration),
        }),
      );
      if (mapping.unknown === true && !this.unknownStatusesWarned.has(mapping.raw)) {
        // T2.2: a status this adapter does not know is reported as `error`, never guessed.
        // CR-2: one warning per distinct raw status, so a future status shared by many
        // tests cannot spend the capped warning budget that identity warnings need; every
        // attempt still carries `ext.normalized: 'error'`. SEC-3: the raw status is made
        // printable inside the message; the envelope `status` stays verbatim.
        this.unknownStatusesWarned.add(mapping.raw);
        const location = locationOf(test);
        this.warn({
          code: 'unknown_status',
          message: `unknown Playwright status '${printable(mapping.raw)}' for ${location}; reported as error`,
          path: location,
        });
      }
      if (this.stepsEnabled) {
        // PS6: walk the step tree lazily, appending per step, so the working set stays
        // at the flush threshold instead of materialising a step array and a draft array.
        for (const step of walkSteps(result.steps)) {
          writer.append(
            this.draft({
              type: 'test.step',
              attempt_no: attemptNo,
              execution_key: identity.execution_key,
              ext: this.stepExt(step, identity),
              ...durationField(step.duration),
            }),
          );
        }
      }
      // CR2: one durable batch per test.
      writer.flush();
    });
  }

  /**
   * Emits the `unmapped` summary and the `run_end` marker, then closes the spool. Returns
   * `{ status: 'failed' }` when any test lacked a complete identity (no `case_id`, no
   * `variant_hash`, no `target_env`, or a conflict), unless `EVIDGATE_ALLOW_UNMAPPED=1`
   * (AC2/S3); Playwright awaits the return value and overrides the run status with it
   * (node_modules/playwright/lib/runner/index.js:1644). The override applies only to a
   * `passed` run — an `interrupted`/`timedout` run keeps its own status (C3). The gate
   * also writes one `[evidgate]` line to stderr, naming the per-cause counts, so a CI log
   * explains the failure. A buffered write failure still throws synchronously.
   *
   * The declared return type is `void`, not the interface's
   * `Promise<{status?} | undefined | void> | void`: the reporter must keep throwing
   * synchronously from `onEnd` (a buffered spool failure), and a Promise-typed return
   * would turn every synchronous call site into a floating promise. The status object is
   * returned regardless — TypeScript erases the assertion, and Playwright awaits whatever
   * comes back (`node_modules/playwright/lib/runner/index.js`).
   */
  onEnd(result: FullResult): void {
    const writer = this.writer;
    if (writer !== undefined) {
      this.guard(() => {
        if (this.unidentifiedCount > 0) {
          // C4/P3: the summary bypasses the 100-envelope cap, so the counts that explain the
          // failure survive even when the run already emitted MAX_WARNING_ENVELOPES warnings.
          this.emitWarning({
            code: 'unmapped',
            message:
              `${this.unidentifiedCount} test(s) without a complete identity ` +
              `(${this.identityCauses()})`,
            count: this.unidentifiedCount,
          });
        }
        if (this.suppressedWarnings > 0) {
          this.emitWarning({
            code: 'warnings_truncated',
            message: `${this.suppressedWarnings} warning(s) suppressed by the reporter's warning caps`,
            count: this.suppressedWarnings,
          });
        }
        writer.append(
          this.draft({
            type: 'run.log',
            attempt_no: 1,
            execution_key: PLACEHOLDER_EXECUTION_KEY,
            status: result.status,
            ext: { schema: EXT_SCHEMA, marker: 'run_end' },
          }),
        );
      });
    }
    const gateTripped = !this.allowUnmapped && this.unidentifiedCount > 0;
    if (gateTripped) {
      // C3: one line, so a run that fails on identity says why in the CI log.
      process.stderr.write(
        `[evidgate] ${this.unidentifiedCount} test(s) without a complete identity ` +
          `(${this.identityCauses()}); set EVIDGATE_ALLOW_UNMAPPED=1 to allow\n`,
      );
    }
    let closeError: Error | null = null;
    if (writer !== undefined) {
      try {
        writer.close();
      } catch (error) {
        closeError = toError(error);
      }
    }
    const failure = this.bufferedError ?? closeError;
    if (failure !== null) throw failure;
    if (gateTripped && result.status === 'passed') {
      return { status: 'failed' } as unknown as void;
    }
    return undefined;
  }

  /**
   * The writer to emit into, or undefined when this reporter emits nothing: a
   * `merge-reports` replay (AC3), or a run whose `onBegin` was refused before the writer
   * existed (the refusal is re-thrown from `onEnd`).
   */
  private activeWriter(): SpoolWriter | undefined {
    if (this.mergeReplay) return undefined;
    return this.writer;
  }

  /**
   * The producer identity this run writes under (T54/AC1/AC2).
   *
   * Precedence: an explicit `producer_id` option wins over the derivation, but only when it
   * agrees with Playwright's shard. With a shard config the two must match: two shards
   * writing under one producer would interleave one sequence, and a wrong id is a server
   * 422 `unknown_producer` anyway, so the run is refused before any envelope instead of
   * failing later per batch. Without a shard config there is nothing to compare against:
   * an explicit id is honoured as before (04.3.1-T1 configs), and the writer records no
   * shard slot; omitting the option derives `shard-1` (T54) and records slot 1.
   */
  private resolveProducer(config?: FullConfig): { producer_id: string; shard?: SpoolShard } {
    const derived = resolveProducerId(config);
    const producerId = this.explicitProducerId ?? derived.producer_id;
    if (
      config?.shard !== undefined &&
      config.shard !== null &&
      producerId !== derived.producer_id
    ) {
      throw new Error(
        `@evidgate/playwright-reporter: the producer_id option ` +
          `${JSON.stringify(this.explicitProducerId)} conflicts with the shard Playwright ` +
          `selected (${String(config.shard.current)}/${String(config.shard.total)}): this ` +
          `shard's manifest slot is ${JSON.stringify(derived.producer_id)} (T54). Drop the ` +
          'option to derive it, or pass that id; another id would be rejected by the server ' +
          '(422 unknown_producer).',
      );
    }
    // The slot is asserted only when the producer *is* the derived slot: a custom id on an
    // unsharded run claims no slot (the manifest owns the shard count, §4).
    return producerId === derived.producer_id
      ? { producer_id: producerId, shard: shardOption(derived) }
      : { producer_id: producerId };
  }

  /**
   * AC3: a `merge-reports` replay re-emits nothing. Each event already reached the spool
   * under its own shard's `producer_id` and contiguous sequence, and the replay exposes no
   * per-event shard identity to a custom reporter, so any id here would be a guess and any
   * renumbering would corrupt order. One stderr line says so, once per process.
   */
  private noteMergeReplay(): void {
    if (this.mergeNoticeWritten) return;
    this.mergeNoticeWritten = true;
    process.stderr.write(
      '[evidgate] merge-reports replay: not emitting envelopes. Every shard already ' +
        'ingested its own events under producer shard-<index>; remove the evidgate reporter ' +
        'from the merge config to silence this line.\n',
    );
  }

  private draft(input: EnvelopeDraft): UnsignedEnvelope {
    const producerId = this.producerId;
    if (producerId === undefined) {
      throw new Error('@evidgate/playwright-reporter: envelope built before the producer id');
    }
    return buildEnvelope({ producer_id: producerId, ...input });
  }

  /** The resolved identity: the injected resolver wins, else the built-in mapping. */
  private identityOf(test: TestCase): Identity {
    if (this.identity !== undefined) return this.identity(test);
    this.resolver ??= createIdentityResolver({
      targetEnv: this.targetEnv,
      warn: (warning) => this.warn(warning),
      variantCache: this.variantCache,
      targetEnvWarned: this.targetEnvWarned,
    });
    return this.resolver(test);
  }

  /**
   * Counts each distinct test once. `unidentifiedCount` counts a test once when any of the
   * three identity fields is missing; the per-cause counters are independent, so one test
   * can be counted in several of them — a conflicted test has no `case_id` and is counted
   * in both `conflictCount` and `noCaseIdCount`.
   */
  private noteIdentity(test: TestCase, identity: Identity): void {
    const key = testKey(test);
    if (this.seenTests.has(key)) return;
    this.seenTests.add(key);
    if (identity.identity_conflict === true) this.conflictCount += 1;
    if (identity.case_id === undefined) this.noCaseIdCount += 1;
    if (identity.variant_hash === undefined) this.noVariantHashCount += 1;
    if (identity.target_env === undefined) this.noTargetEnvCount += 1;
    if (
      identity.case_id === undefined ||
      identity.variant_hash === undefined ||
      identity.target_env === undefined
    ) {
      this.unidentifiedCount += 1;
    }
  }

  /**
   * The per-cause breakdown of the identity gate, appended to the `unmapped` summary and
   * the stderr line so a CI log reader can tell which fix applies (a Playwright project
   * name that is not a valid target env, for example, is fixed by `EVIDGATE_TARGET_ENV`).
   */
  private identityCauses(): string {
    return (
      `${this.conflictCount} conflicted, ${this.noCaseIdCount} no case_id, ` +
      `${this.noVariantHashCount} no variant_hash, ${this.noTargetEnvCount} no target_env`
    );
  }

  /** Emits one warning envelope, up to the cap; the overflow is counted, not emitted. */
  private warn(warning: WarningPayload): void {
    if (this.warningCount >= MAX_WARNING_ENVELOPES) {
      this.suppressedWarnings += 1;
      return;
    }
    this.warningCount += 1;
    this.emitWarning(warning);
  }

  private emitWarning(warning: WarningPayload): void {
    const writer = this.activeWriter();
    if (writer === undefined) return;
    writer.append(
      this.draft({
        type: 'run.log',
        attempt_no: 1,
        execution_key: PLACEHOLDER_EXECUTION_KEY,
        ext: { schema: EXT_SCHEMA, warn: warning },
      }),
    );
  }

  /**
   * The `ext` of a test envelope. The four status keys are set explicitly — `undefined`
   * (absent) on `test.started` — after the Playwright members, so a same-named member of
   * `TestResult` can never reach `ext` in their place (04.3.2-T2.1).
   */
  private testExt(
    test: TestCase,
    result: TestResult,
    identity: Identity,
    mapping?: StatusMapping,
  ): ExtPayload {
    return this.applyExtFilter({
      ...ownMembers(test),
      ...ownMembers(result),
      schema: EXT_SCHEMA,
      file: test.location.file,
      project: test.parent.project()?.name,
      engine: this.engineVersion,
      case_id: identity.case_id,
      variant_hash: identity.variant_hash,
      target_env: identity.target_env,
      identity_conflict: identity.identity_conflict,
      normalized: mapping?.normalized,
      skip_reason: mapping?.skip_reason,
      xfail: mapping?.xfail,
      error_summary: mapping?.error_summary,
    });
  }

  private stepExt(step: TestStep, identity: Identity): ExtPayload {
    const candidate: Record<string, unknown> = {
      ...ownMembers(step),
      schema: EXT_SCHEMA,
      case_id: identity.case_id,
      variant_hash: identity.variant_hash,
      target_env: identity.target_env,
      identity_conflict: identity.identity_conflict,
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
 * The writer's `shard` option (04.3.3-T1): the resolved manifest slot, with `total` only
 * when Playwright declared one. An unsharded run passes `{ index: 1 }` — the record then
 * carries the slot but no total, because the manifest owns the shard count.
 */
function shardOption(resolved: ResolvedProducerId): SpoolShard {
  return resolved.shard_total === undefined
    ? { index: resolved.shard_index }
    : { index: resolved.shard_index, total: resolved.shard_total };
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

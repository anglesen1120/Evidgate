// 04.3.1-T1.1/T1.2: the reporter Playwright loads from
// `reporter: [['@evidgate/playwright-reporter', { spool_dir, run_id, producer_id }]]`.
// It implements the four documented callbacks (onBegin/onTestBegin/onTestEnd/onEnd) and
// emits one 00 §9.4 envelope per callback into the spool; it never opens a socket
// (04 §7). Construction is fail-fast: missing/invalid options, an unsupported engine
// (AC5) all throw before any callback runs. After construction the callbacks never throw:
// a spool failure is buffered and reported from onEnd as `{ status: 'failed' }` plus one
// `[evidgate] reporter failure` stderr line, so the run fails instead of losing evidence
// silently (task file §5).
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
// therefore buffered from `onBegin` and failed from `onEnd` like every other callback
// failure, instead of at construction. In a `merge-reports` replay the reporter emits
// nothing: each event already reached the spool under its own shard's producer id and
// sequence, and the replay exposes no per-event shard identity (AC3).
//
// 04.3.3-T2.2: attachments. When `result.attachments` is EMPTY the callback is the 04.3.1
// path unchanged: one `test.finished`, its steps, one flush, all synchronous. When it is
// not, `attach.ts` uploads them (AC5–AC7) and this file HOLDS the drafted batch until every
// upload of that attempt settled, then appends `test.finished` (with `artifact_ids`) and its
// steps in one go. Held batches are appended in test-end order AMONG ATTEMPTS WITH
// ATTACHMENTS; an attempt without attachments is appended immediately, so it may precede an
// earlier test whose uploads are still in flight. Sequence numbers stay contiguous either
// way: they are assigned at append time, never at draft time.
// Warnings (`attachment_*`, `shards_*`) go through the capped `warn()` path. `onEnd`
// therefore has two shapes: with nothing pending it is the synchronous callback of 04.3.1;
// with uploads pending it returns a Promise that waits for them under
// `attachment_drain_deadline_ms` (default 120 s, NFR-04-1) and then appends the held
// batches. The deadline stops the WAIT only: an upload whose PUT
// already started runs until its own deadline and is abandoned at its next checkpoint, never
// cancelled mid-PUT; one `attachment_drain_deadline` warning summarises every abandoned
// attempt. Then the same end sequence runs in both shapes, and a buffered failure fails the
// run through the returned status instead of a throw. A merge-replay or a
// reporter without a writer never touches attachments at all.
import { delimiter } from 'node:path';

import type {
  FullConfig,
  FullResult,
  Reporter,
  Suite,
  TestCase,
  TestResult,
  TestStep,
} from '@playwright/test/reporter';

import {
  AttachmentPipeline,
  type AttachmentRecord,
  type AttachmentWarning,
  type CliModule,
} from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import { defaultAttachmentRoots } from './attachPaths.js';
import { buildCapabilityDocument } from './capability.js';
import { DEFAULT_DRAIN_DEADLINE_MS } from './drain.js';
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
  /**
   * 04.3.3-T2.2: extra allow-listed attachment directories (AC6), appended after the
   * Playwright output directories and `<rootDir>/test-results`. `EVIDGATE_ATTACHMENT_DIRS`
   * (path-delimiter separated) adds to this list; both are read once in the constructor.
   */
  attachment_dirs?: string[];
  /**
   * 04.3.3-T2.2: the `onEnd` attachment drain deadline in milliseconds (NFR-04-1). Default
   * {@link DEFAULT_DRAIN_DEADLINE_MS} (120 s); `onEnd` never blocks past it.
   */
  attachment_drain_deadline_ms?: number;
  /**
   * Test-only seam (04.3.3-T2.2): the ONE lazy `@evidgate/cli` load the attachment pipeline
   * performs. Defaults to the real dynamic import; a spec injects a fake module surface.
   */
  importCli?: () => Promise<CliModule>;
}

interface EnvelopeDraft {
  type: EventType;
  attempt_no: number;
  execution_key: string;
  ext: ExtPayload;
  status?: string;
  duration_ms?: number;
  /** 04.3.3-T2.2: the artifacts a `test.finished` references; absent unless non-empty. */
  artifact_ids?: string[];
}

/** Everything `test.finished` + its steps need, held until the attempt's uploads settled. */
interface AttemptDraft {
  test: TestCase;
  result: TestResult;
  identity: Identity;
  mapping: StatusMapping;
  attemptNo: number;
  artifactIds?: readonly string[];
  records?: readonly AttachmentRecord[];
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
  /** Extra attachment roots: the option plus `EVIDGATE_ATTACHMENT_DIRS`, read once here. */
  private readonly attachmentDirs: readonly string[];
  private readonly attachmentDrainDeadlineMs: number;
  private readonly importCli: (() => Promise<CliModule>) | undefined;
  /**
   * The attachment pipeline of this producer (04.3.3-T2.2), built at `onBegin` because the
   * allow-list needs the resolved config. Undefined in a merge-replay or a refused `onBegin`,
   * which is exactly when attachments must not be touched.
   */
  private pipeline: AttachmentPipeline | undefined;
  /** Serialises the appends of held batches so they land in test-end order (contiguous). */
  private attachChain: Promise<void> = Promise.resolve();
  /** Attempts whose uploads are still running or whose batch has not been appended yet. */
  private attachmentsQueued = 0;
  private unavailableWarned = false;

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

    // 04.3.3-T2.2: the extra attachment roots are read once here, with every other env value.
    // The path delimiter is the platform's (`:` on POSIX), so a Windows CI job splits its own
    // way; an empty segment is dropped rather than resolving to the root directory.
    const envDirs = process.env.EVIDGATE_ATTACHMENT_DIRS;
    this.attachmentDirs = [
      ...(options.attachment_dirs ?? []),
      ...(envDirs === undefined || envDirs.length === 0
        ? []
        : envDirs
            .split(delimiter)
            .map((dir) => dir.trim())
            .filter((dir) => dir.length > 0)),
    ];
    this.attachmentDrainDeadlineMs =
      options.attachment_drain_deadline_ms ?? DEFAULT_DRAIN_DEADLINE_MS;
    this.importCli = options.importCli;
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

      // 04.3.3-T2.2: the attachment pipeline (AC4–AC7). Its allow-list is the one the CLI's
      // own upload guard receives (AC6): every Playwright `outputDir`, `<rootDir>/test-results`
      // and the configured extra directories, resolved once here.
      const rootDir = config?.rootDir ?? process.cwd();
      this.pipeline = new AttachmentPipeline({
        roots: defaultAttachmentRoots({
          outputDirs: (config?.projects ?? []).map((project) => project.outputDir),
          rootDir,
          extraDirs: this.attachmentDirs,
        }),
        cwd: rootDir,
        deadlineMs: this.attachmentDrainDeadlineMs,
        env: process.env,
        ...(this.importCli === undefined ? {} : { importCli: this.importCli }),
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
      // 04.3.3-T2: the writer's once-per-run notices (a `shards.json` floor ahead of the
      // chunks, a record write failure at rotation) become `run.log` warnings.
      this.emitSpoolNotices();
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
      const attachments = result.attachments ?? [];
      const attempt: AttemptDraft = { test, result, identity, mapping, attemptNo };

      // 04.3.3-T2.2: a test without attachments keeps the 04.3.1 path byte-identically —
      // same drafts, same flush, still entirely synchronous.
      if (attachments.length === 0) {
        this.appendAttempt(writer, attempt);
        this.emitSpoolNotices();
        return;
      }

      const pipeline = this.pipeline;
      const producerId = this.producerId;
      if (pipeline === undefined || producerId === undefined) {
        // A merge-replay or a refused `onBegin` never reaches here; this is the defensive
        // branch, and the batch is emitted exactly as before with one run-level warning.
        this.warnUnavailable();
        this.appendAttempt(writer, attempt);
        this.emitSpoolNotices();
        return;
      }

      if (identity.execution_key === PLACEHOLDER_EXECUTION_KEY) {
        // T17: the server scopes an artifact to the planned `(execution_key, attempt_no)`,
        // and the placeholder names no planned attempt — so nothing is uploaded (one warning
        // per test) and the result is emitted at once, without `artifact_ids`.
        this.warn({
          code: 'attachment_no_execution_key',
          message:
            `${String(attachments.length)} attachment(s) skipped: this attempt has no planned ` +
            'execution key (the identity resolves to the placeholder)',
          path: locationOf(test),
          count: attachments.length,
        });
        this.appendAttempt(writer, attempt);
        this.emitSpoolNotices();
        return;
      }

      this.holdAttempt(
        writer,
        attempt,
        attachments,
        hasSensitiveAnnotation(test, result),
        producerId,
      );
    });
  }

  /**
   * One `test.finished` (with `artifact_ids` and `ext.attachments` when the attempt uploaded),
   * the status warning, every step, and one flush — the batch the 04.3.1 callback emitted, now
   * shared by the synchronous and the held path so they can never drift.
   */
  private appendAttempt(writer: SpoolWriter, attempt: AttemptDraft): void {
    const { test, result, identity, mapping, attemptNo } = attempt;
    // Only completed uploads, deduplicated, in attachment order: the id list must never
    // reference an artifact the server did not confirm (AC5).
    const artifactIds = [...new Set(attempt.artifactIds ?? [])];
    writer.append(
      this.draft({
        type: 'test.finished',
        attempt_no: attemptNo,
        execution_key: identity.execution_key,
        status: mapping.raw,
        ext: this.testExt(test, result, identity, mapping, attempt.records),
        ...durationField(result.duration),
        ...(artifactIds.length === 0 ? {} : { artifact_ids: artifactIds }),
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
  }

  /**
   * Starts the attempt's uploads NOW (the drain keeps at most two in flight, across tests) and
   * holds its drafts until they settled. Only the append is serialised, through
   * {@link attachChain}, so no held batch can overtake another held batch; every `sequence`
   * is assigned at append time, in test-end order among the attempts that carry attachments.
   * (An attempt WITHOUT attachments is appended immediately and may precede a held one.)
   */
  private holdAttempt(
    writer: SpoolWriter,
    attempt: AttemptDraft,
    attachments: readonly PlaywrightAttachment[],
    sensitiveAnnotated: boolean,
    producerId: string,
  ): void {
    const pipeline = this.pipeline;
    if (pipeline === undefined) return;
    const work = pipeline.process({
      attachments,
      sensitiveAnnotated,
      runId: this.runId,
      producerId,
      executionKey: attempt.identity.execution_key,
      attemptNo: attempt.attemptNo,
    });
    this.attachmentsQueued += 1;
    this.attachChain = this.attachChain.then(async () => {
      try {
        const outcome = await work;
        for (const warning of outcome.warnings) this.warn(warning);
        this.appendAttempt(writer, {
          ...attempt,
          artifactIds: outcome.artifactIds,
          records: outcome.records,
        });
        this.emitSpoolNotices();
      } catch (error) {
        // A spool failure while appending a held batch is buffered like every other callback
        // failure, and `onEnd` fails the run for it instead of throwing.
        this.bufferedError ??= toError(error);
      } finally {
        this.attachmentsQueued -= 1;
      }
    });
  }

  /**
   * Emits the `unmapped` summary and the `run_end` marker, then closes the spool. Returns
   * `{ status: 'failed' }` when any test lacked a complete identity (no `case_id`, no
   * `variant_hash`, no `target_env`, or a conflict), unless `EVIDGATE_ALLOW_UNMAPPED=1`
   * (AC2/S3), and likewise when a spool failure was buffered or the spool could not be
   * closed. Playwright awaits the return value and overrides the run status with it
   * (node_modules/playwright/lib/runner/index.js:1644). The override applies only to a
   * `passed` run — an `interrupted`/`timedout` run keeps its own status (C3). The identity
   * gate also writes one `[evidgate]` line to stderr, naming the per-cause counts, so a CI
   * log explains the failure.
   *
   * 04.3.3-T2.2 split: with no attachment work pending the end sequence runs synchronously;
   * with uploads pending it runs after {@link endAfterDrain} drains them. Neither shape
   * throws: Playwright logs a throwing reporter as an internal reporter error and only fails
   * the run through its own error bookkeeping, while the returned status is the documented
   * failure channel (and a caller that drops reporter errors cannot turn the loss into a
   * passing run). `Reporter` types no synchronous non-void return, so a failure is boxed in
   * a resolved promise — the value Playwright awaits — and a passing run returns nothing.
   */
  onEnd(result: FullResult): Promise<{ status: 'failed' } | undefined> | undefined {
    if (this.attachmentsQueued === 0 && !(this.pipeline?.hasPending ?? false)) {
      const failure = this.finishRun(result);
      return failure === undefined ? undefined : Promise.resolve(failure);
    }
    return this.endAfterDrain(result);
  }

  /**
   * The asynchronous `onEnd` (04.3.3-T2.2): drains the attachment pipeline under the deadline,
   * appends the held batches, emits ONE `attachment_drain_deadline` warning naming how many
   * attempts lost their `artifact_ids`, then runs the synchronous end sequence. Never blocks
   * past `attachment_drain_deadline_ms`; a buffered failure fails the run, never the promise.
   */
  private async endAfterDrain(result: FullResult): Promise<{ status: 'failed' } | undefined> {
    try {
      const pipeline = this.pipeline;
      const drained =
        pipeline === undefined
          ? { timedOut: false, abandoned: 0, deadlineAttempts: 0 }
          : await pipeline.finish();
      // Every held batch is appended before the end markers: a late test must never land
      // after `run_end` (the server would read it as a post-run event).
      await this.attachChain;
      if (drained.deadlineAttempts > 0 || drained.abandoned > 0) {
        this.warn({
          code: 'attachment_drain_deadline',
          message:
            `the attachment drain deadline (${String(this.attachmentDrainDeadlineMs)} ms) ` +
            `abandoned ${String(drained.deadlineAttempts)} attempt(s); an abandoned ` +
            'attachment has no artifact_id in its test.finished envelope',
          count: drained.deadlineAttempts,
        });
      }
      this.emitSpoolNotices();
    } catch (error) {
      // The drain never rejects; an append failure is already buffered by `holdAttempt`.
      this.bufferedError ??= toError(error);
    }
    return this.finishRun(result);
  }

  /** The end sequence itself: summary, `run_end`, close, identity gate, spool-failure report. */
  private finishRun(result: FullResult): { status: 'failed' } | undefined {
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
    if (failure !== null) this.reportBufferedFailure(failure);
    // Both causes fail a passed run; an already failed, timed out or interrupted run keeps
    // its own status, and therefore its own non-zero exit code.
    if (result.status === 'passed' && (gateTripped || failure !== null)) {
      return { status: 'failed' };
    }
    return undefined;
  }

  /**
   * Reports a buffered callback failure — a spool write or close failure, a refused producer
   * id, a throwing identity resolver — through the `onEnd` result instead of a throw,
   * because Playwright only routes a throwing reporter into its internal reporter error
   * bookkeeping. Only an errno/reason code leaves the process: the message can carry a
   * resolver-supplied value, a presigned URL or the spool path, and none of those belong in
   * a CI log.
   */
  private reportBufferedFailure(failure: Error): void {
    process.stderr.write(
      `[evidgate] reporter failure (${failureCode(failure)}); the run's evidence is incomplete\n`,
    );
  }

  /**
   * The writer to emit into, or undefined when this reporter emits nothing: a
   * `merge-reports` replay (AC3), or a run whose `onBegin` was refused before the writer
   * existed (the refusal fails the run at `onEnd`).
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
      throw new ProducerShardConflictError(
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

  /**
   * Emits one warning envelope, up to the cap; the overflow is counted, not emitted. The
   * payload is either an identity warning (`WarningPayload`) or an attachment/spool warning
   * (`AttachmentWarning`, 04.3.3-T2.2) — both are `ext.warn` objects with the same shape.
   */
  private warn(warning: WarningPayload | AttachmentWarning): void {
    if (this.warningCount >= MAX_WARNING_ENVELOPES) {
      this.suppressedWarnings += 1;
      return;
    }
    this.warningCount += 1;
    this.emitWarning(warning);
  }

  private emitWarning(warning: WarningPayload | AttachmentWarning): void {
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

  /** ONE `attachments_unavailable` warning per run: no pipeline, so nothing is uploaded. */
  private warnUnavailable(): void {
    if (this.unavailableWarned) return;
    this.unavailableWarned = true;
    this.warn({
      code: 'attachments_unavailable',
      message: 'the attachment pipeline is unavailable for this run; attachments are skipped',
    });
  }

  /**
   * Turns the writer's once-per-writer notices into `run.log` warnings (04.3.3-T2, handed
   * over from T1). `takeNotices()` is a drain — it clears what it returns — and the writer
   * already guarantees one notice per code per run, so calling this after every flush that can
   * rotate cannot duplicate a warning. The messages are path-free by construction.
   */
  private emitSpoolNotices(): void {
    const writer = this.activeWriter();
    if (writer === undefined) return;
    for (const notice of writer.takeNotices()) {
      this.warn({ code: notice.code, message: notice.message });
    }
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
    records?: readonly AttachmentRecord[],
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
      // 04.3.3-T2.2: base names, kinds, outcomes and artifact ids only — the allow-list
      // (`extFilter.ts`) is what lets the member through, and the record shape carries no
      // path, no URL and no token.
      attachments: records,
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
    // SEC/F1: `attachments` is allow-listed for `test.finished` ONLY, where `testExt` sets it
    // from our own `AttachmentRecord` rows (base name, kind, outcome, code, artifact id). A
    // raw Playwright `TestStep.attachments` is `{name, contentType, path, body}` — an absolute
    // path and the attachment bytes in memory — so a step envelope must never carry one.
    delete candidate.attachments;
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

/**
 * AC7: the suite (test) or the case (result) opted in through the `evidgate.sensitive`
 * annotation. Presence is the opt-in, whatever value the annotation carries — a suite that
 * annotated the case at all meant to mark it sensitive, and the flag is never downgraded.
 */
function hasSensitiveAnnotation(test: TestCase, result: TestResult): boolean {
  for (const annotation of [...(test.annotations ?? []), ...(result.annotations ?? [])]) {
    if (annotation.type === 'evidgate.sensitive') return true;
  }
  return false;
}

function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

/**
 * The one part of a buffered failure that is safe to log: a POSIX errno or one of the
 * adapter's own fixed reason codes. Anything else — a resolver's message, an upload error
 * carrying a presigned URL, a caller-crafted `code` — degrades to a constant label, so no
 * value a caller controls can reach the line.
 */
function failureCode(failure: Error): string {
  const code: unknown = (failure as NodeJS.ErrnoException).code;
  if (typeof code !== 'string') return UNEXPECTED_FAILURE;
  return SAFE_FAILURE_CODES[code] === true || ERRNO_CODE.test(code) ? code : UNEXPECTED_FAILURE;
}

/** The adapter's own reason codes (`spool.ts`, `writerLock.ts`) plus the shard conflict. */
const SAFE_FAILURE_CODES: Readonly<Record<string, true>> = {
  invalid_config: true,
  spool_cap_exceeded: true,
  spool_shard_mismatch: true,
  invalid_shard: true,
  spool_writer_locked: true,
  spool_unsafe_path: true,
  producer_id_shard_conflict: true,
};

/** A POSIX errno name; the node fs errors the spool can raise all use this shape. */
const ERRNO_CODE = /^E[A-Z]{2,15}$/;

const UNEXPECTED_FAILURE = 'unexpected_error';

/** An explicit `producer_id` that disagrees with the shard Playwright selected. */
class ProducerShardConflictError extends Error {
  readonly code = 'producer_id_shard_conflict';
}

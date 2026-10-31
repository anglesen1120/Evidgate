// The reporter Playwright loads from `reporter: [['@evidgate/playwright-reporter', {...}]]`.
// It never opens a socket: each callback drafts envelopes and hands them to the spool writer.
// Construction is fail-fast (a missing option, an unsafe producer id or an unsupported engine
// throws before any callback runs), and afterwards the callbacks never throw — a failure is
// buffered and reported from `onEnd`, so a broken spool fails the run instead of losing
// evidence silently. The work is split by responsibility: this file wires the callbacks,
// while the options, identity, envelope, warning, attachment and end-sequence rules live in
// the sibling `reporter*.ts` modules.
//
// `onEnd` has two shapes. With nothing pending it is the synchronous callback; with
// attachment uploads pending it returns a Promise that waits for them under the drain
// deadline. The deadline stops the WAIT only — a PUT that already started runs to its own
// deadline — and held batches are appended in test-end order before the end markers.
// Sequence numbers are assigned at append time, never at draft time, so they stay contiguous
// in either shape.
import type {
  FullConfig,
  FullResult,
  Reporter,
  Suite,
  TestCase,
  TestResult,
} from '@playwright/test/reporter';

import type { AttachmentPipeline, CliModule } from './attach.js';
import { buildCapabilityDocument } from './capability.js';
import { EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import {
  createIdentityResolver,
  locationOf,
  PLACEHOLDER_EXECUTION_KEY,
  type IdentityResolver,
} from './identity.js';
import { AttemptAppender, type AttemptDraft } from './reporterAttempt.js';
import {
  AttachmentCoordinator,
  buildAttachmentPipeline,
  hasSensitiveAnnotation,
} from './reporterAttachments.js';
import { draftEnvelope, type EnvelopeDraft } from './reporterDraft.js';
import { RunEndSequence } from './reporterEnd.js';
import { TestExtBuilder } from './reporterExt.js';
import { toError } from './reporterFailure.js';
import { IdentityBookkeeping } from './reporterIdentity.js';
import { resolveReporterSettings, type ReporterSettings } from './reporterOptions.js';
import { resolveProducerIdentity } from './reporterProducer.js';
import { ReporterWarnings, type ReporterWarning } from './reporterWarnings.js';
import { loadSidecarIndex, type SidecarFs } from './sidecar.js';
import { SpoolWriter, type SpoolWriterOptions } from './spool.js';
import { attemptOf, mapStatus } from './status.js';

export { MAX_WARNING_ENVELOPES } from './reporterOptions.js';

export interface EvidgateReporterOptions {
  /** Spool root; falls back to `EVIDGATE_SPOOL_DIR`. */
  spool_dir?: string;
  /** Run id; falls back to `EVIDGATE_RUN_ID`. */
  run_id?: string;
  /**
   * Producer id override. Without it the id is derived from Playwright's shard config at
   * `onBegin` (`shard-<current>`, `shard-1` unsharded). An explicit value must equal the
   * derived id when Playwright selected a shard, or the run is refused; see
   * `contracts/adapter/playwright-mapping.md`. There is deliberately no env fallback.
   */
  producer_id?: string;
  /** Emit `test.step` envelopes for `result.steps`. Default true. */
  steps?: boolean;
  /** Capability `mode`. Default `full`. */
  mode?: 'full' | 'ingest_only';
  /** Execution-identity resolver; overrides the built-in sidecar/annotation mapping. */
  identity?: IdentityResolver;
  /** Test-only seam: injects the sidecar filesystem reader. */
  sidecarFs?: SidecarFs;
  /** Test-only seam: overrides the detected @playwright/test version for the guard. */
  engineVersion?: string;
  /** Test-only seam: the CLI arguments that recognise a `merge-reports` replay. */
  argv?: readonly string[];
  /**
   * Test-only seam: the spool writer's single-writer lock. The restart test needs a writer
   * whose recorded holder pid reads as dead, so a second reporter can take over the stale
   * lock without killing a real process.
   */
  lock?: SpoolWriterOptions['lock'];
  /**
   * Extra allow-listed attachment directories, appended after the Playwright output
   * directories and `<rootDir>/test-results`. `EVIDGATE_ATTACHMENT_DIRS` adds to this list;
   * both are read once in the constructor.
   */
  attachment_dirs?: string[];
  /**
   * The `onEnd` attachment drain deadline in milliseconds; default 120 s. `onEnd` never
   * waits past it.
   */
  attachment_drain_deadline_ms?: number;
  /**
   * Test-only seam: the ONE lazy `@evidgate/cli` load the attachment pipeline performs.
   * Defaults to the real dynamic import; a spec injects a fake module surface.
   */
  importCli?: () => Promise<CliModule>;
}

export class EvidgateReporter implements Reporter {
  private readonly settings: ReporterSettings;
  private readonly ext: TestExtBuilder;
  private readonly identity: IdentityBookkeeping;
  private readonly warnings: ReporterWarnings;
  private readonly attempts: AttemptAppender;
  private readonly attachments: AttachmentCoordinator;
  private readonly end: RunEndSequence;
  /** Resolved at `onBegin`: the explicit option or the derived `shard-<index>`. */
  private producerId: string | undefined;
  /**
   * Opened at `onBegin`, because the shard — and therefore the producer id — is only known
   * from the resolved config there. Undefined means "this reporter emits nothing": a
   * `merge-reports` replay, or an `onBegin` that was refused.
   */
  private writer: SpoolWriter | undefined;
  private pipeline: AttachmentPipeline | undefined;
  /** True in a `merge-reports` replay: every event was already ingested per shard. */
  private mergeNoticeWritten = false;
  private bufferedError: Error | null = null;

  constructor(options: EvidgateReporterOptions = {}) {
    const settings = resolveReporterSettings(options);
    this.settings = settings;
    this.ext = new TestExtBuilder(settings.engineVersion);
    this.warnings = new ReporterWarnings((warning) => this.appendWarning(warning));
    this.identity = new IdentityBookkeeping({
      targetEnv: settings.targetEnv,
      injected: settings.identity,
      allowUnmapped: settings.allowUnmapped,
      warn: (warning) => this.warnings.warn(warning),
    });
    this.attempts = new AttemptAppender({
      ext: this.ext,
      draft: (input) => this.draft(input),
      warn: (warning) => this.warnings.warn(warning),
      stepsEnabled: settings.stepsEnabled,
    });
    this.attachments = new AttachmentCoordinator({
      runId: settings.runId,
      deadlineMs: settings.attachmentDrainDeadlineMs,
      producerId: () => this.producerId,
      pipeline: () => this.pipeline,
      appendAttempt: (writer, attempt) => this.attempts.append(writer, attempt),
      warn: (warning) => this.warnings.warn(warning),
      emitNotices: () => this.emitSpoolNotices(),
      bufferFailure: (error) => this.bufferFailure(error),
    });
    this.end = new RunEndSequence({
      writer: () => this.writer,
      draft: (input) => this.draft(input),
      emitWarning: (warning) => this.appendWarning(warning),
      identity: this.identity,
      warnings: this.warnings,
      bufferFailure: (error) => this.bufferFailure(error),
      bufferedFailure: () => this.bufferedError,
    });
  }

  /** How many secret-looking `ext` keys were dropped. */
  get secretKeysDropped(): number {
    return this.ext.secretKeysDropped;
  }

  /**
   * Playwright adds its default console reporter only when no configured reporter prints to
   * stdio. This reporter writes to the spool, not stdout, so returning false keeps the
   * line/dot reporter and the run summary visible: a run with only this reporter must not
   * be silent.
   */
  printsToStdio(): boolean {
    return false;
  }

  /**
   * Sequence 1 is the capability document and sequence 2 the `run_begin` marker, so the
   * sidecar warnings and a pending `invalid_target_env` come after the markers. The shard's
   * producer identity is resolved here too, because only the config knows it; that is also
   * why the writer is opened here. A `merge-reports` replay emits nothing at all.
   */
  onBegin(config?: FullConfig, suite?: Suite): void {
    this.guard(() => {
      if (this.settings.mergeReplay) {
        this.noteMergeReplay();
        return;
      }
      const producer = resolveProducerIdentity(this.settings.explicitProducerId, config);
      this.producerId = producer.producer_id;
      this.writer = new SpoolWriter({
        spoolDir: this.settings.spoolDir,
        runId: this.settings.runId,
        producerId: producer.producer_id,
        ...(producer.shard === undefined ? {} : { shard: producer.shard }),
        ...(this.settings.writerLock === undefined ? {} : { lock: this.settings.writerLock }),
      });
      // The attachment allow-list needs the resolved config, so the pipeline is built here.
      this.pipeline = buildAttachmentPipeline(
        {
          attachmentDirs: this.settings.attachmentDirs,
          deadlineMs: this.settings.attachmentDrainDeadlineMs,
          importCli: this.settings.importCli,
        },
        config,
      );
      this.emitBeginMarkers(config, suite);
      this.writer.flush();
      this.emitSpoolNotices();
    });
  }

  onTestBegin(test: TestCase, result: TestResult): void {
    this.guard(() => {
      const writer = this.activeWriter();
      if (writer === undefined) return;
      const identity = this.identity.identityOf(test);
      this.identity.note(test, identity);
      writer.append(
        this.draft({
          type: 'test.started',
          attempt_no: attemptOf(result),
          execution_key: identity.execution_key,
          ext: this.ext.testExt(test, result, identity),
        }),
      );
    });
  }

  onTestEnd(test: TestCase, result: TestResult): void {
    this.guard(() => {
      const writer = this.activeWriter();
      if (writer === undefined) return;
      const identity = this.identity.identityOf(test);
      this.identity.note(test, identity);
      const mapping = mapStatus({
        status: result.status,
        expectedStatus: test.expectedStatus,
        annotations: [...(test.annotations ?? []), ...(result.annotations ?? [])],
        errors: result.errors,
      });
      const attempt: AttemptDraft = {
        test,
        result,
        identity,
        mapping,
        attemptNo: attemptOf(result),
      };
      const attachments = result.attachments ?? [];
      // An attempt without attachments keeps the plain path: same drafts, same flush, still
      // entirely synchronous.
      if (attachments.length === 0) {
        this.appendAttempt(writer, attempt);
        return;
      }
      const pipeline = this.pipeline;
      const producerId = this.producerId;
      if (pipeline === undefined || producerId === undefined) {
        // Defensive only: a merge-replay or a refused `onBegin` never reaches this point.
        this.warnings.warnUnavailableOnce();
        this.appendAttempt(writer, attempt);
        return;
      }
      if (identity.execution_key === PLACEHOLDER_EXECUTION_KEY) {
        // The server scopes an artifact to a planned `(execution_key, attempt_no)` and the
        // placeholder names no planned attempt, so nothing is uploaded and the result is
        // emitted at once, without `artifact_ids`.
        this.warnings.warn({
          code: 'attachment_no_execution_key',
          message:
            `${String(attachments.length)} attachment(s) skipped: this attempt has no planned ` +
            'execution key (the identity resolves to the placeholder)',
          path: locationOf(test),
          count: attachments.length,
        });
        this.appendAttempt(writer, attempt);
        return;
      }
      this.attachments.hold(writer, attempt, attachments, hasSensitiveAnnotation(test, result));
    });
  }

  /**
   * Emits the `unmapped` summary and the `run_end` marker, then closes the spool. Returns
   * `{ status: 'failed' }` when an identity is incomplete, a spool failure was buffered or
   * the spool could not be closed; Playwright awaits the value and overrides the run status
   * with it, but only for a `passed` run, so an interrupted or timed-out run keeps its own
   * status. With uploads pending the end sequence runs after {@link endAfterDrain} drains
   * them. Neither shape throws: the returned status is the documented failure channel.
   */
  onEnd(result: FullResult): Promise<{ status: 'failed' } | undefined> | undefined {
    if (this.attachments.idle) {
      const failure = this.end.finish(result);
      return failure === undefined ? undefined : Promise.resolve(failure);
    }
    return this.endAfterDrain(result);
  }

  /** Drains the uploads, then runs the same end sequence; a buffered failure stays buffered. */
  private async endAfterDrain(result: FullResult): Promise<{ status: 'failed' } | undefined> {
    try {
      await this.attachments.drain();
    } catch (error) {
      // The drain never rejects; an append failure is already buffered by `hold`.
      this.bufferFailure(error);
    }
    return this.end.finish(result);
  }

  private appendAttempt(writer: SpoolWriter, attempt: AttemptDraft): void {
    this.attempts.append(writer, attempt);
    this.emitSpoolNotices();
  }

  /** The writer to emit into, or undefined when this reporter emits nothing. */
  private activeWriter(): SpoolWriter | undefined {
    if (this.settings.mergeReplay) return undefined;
    return this.writer;
  }

  private draft(input: EnvelopeDraft): UnsignedEnvelope {
    return draftEnvelope(this.producerId, input);
  }

  private appendWarning(warning: ReporterWarning): void {
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

  private emitSpoolNotices(): void {
    this.warnings.emitWriterNotices(this.activeWriter());
  }

  /**
   * A `merge-reports` replay re-emits nothing: every event already reached the spool under
   * its own shard's producer id and contiguous sequence, and the replay exposes no
   * per-event shard identity, so any id here would be a guess. One stderr line says so.
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

  private emitBeginMarkers(config: FullConfig | undefined, suite: Suite | undefined): void {
    const writer = this.writer;
    if (writer === undefined) return;
    const testFiles = new Set<string>();
    for (const test of suite?.allTests() ?? []) testFiles.add(test.location.file);
    const index = loadSidecarIndex(
      testFiles,
      this.settings.sidecarFs,
      config?.rootDir === undefined ? {} : { rootDir: config.rootDir },
    );
    // Warnings the sidecar loader discarded past its own cap still count as suppressed, so
    // `warnings_truncated` reports the full loss.
    this.warnings.noteSuppressed(index.droppedWarnings);
    this.identity.install(
      createIdentityResolver({
        sidecar: index,
        targetEnv: this.settings.targetEnv,
        warn: (warning) => this.warnings.warn(warning),
        variantCache: this.identity.variants,
        targetEnvWarned: this.identity.targetEnvWarned,
      }),
    );
    const capability = buildCapabilityDocument({
      engineVersion: this.settings.engineVersion,
      stepEvents: this.settings.stepsEnabled,
      mode: this.settings.mode,
    });
    writer.appendMany([
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
      this.warnings.warn({ code: warning.code, message: warning.message, path: warning.path });
    }
    if (this.settings.targetEnvWarning !== undefined) {
      this.warnings.warn(this.settings.targetEnvWarning);
    }
  }

  /** One durable batch per callback, so a killed run loses at most the current callback. */
  private guard(run: () => void): void {
    try {
      run();
    } catch (error) {
      this.bufferFailure(error);
    }
  }

  private bufferFailure(error: unknown): void {
    this.bufferedError ??= toError(error);
  }
}

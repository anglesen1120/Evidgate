// The attachment pipeline object: the per-attempt mapping/guard/cap/upload flow, the memoised
// uploader load and the drain handover. The reporter calls `process()` per attempt and `finish()`
// at `onEnd`, so no per-test callback ever awaits an upload; every documented refusal becomes a
// record plus a warning instead of an exception.
import type { AttachmentUploader } from '@evidgate/cli';

import {
  isOverCap,
  mapAttachment,
  type MappedAttachment,
  type PlaywrightAttachment,
} from './attachMap.js';
import { createPathGuard, type PathGuard } from './attachPaths.js';
import {
  DEFAULT_DRAIN_DEADLINE_MS,
  UploadDrain,
  type DrainJobContext,
  type JobOutcome,
  type UploadDrainOptions,
} from './drain.js';
import { UUID_PATTERN, errorCode, shownName } from './attachNames.js';
import {
  MAX_ATTACHMENTS_PER_RUN,
  MAX_ATTACHMENTS_PER_TEST,
  type AttachmentFinishResult,
  type AttachmentPipelineOptions,
  type AttachmentProcessInput,
  type AttachmentProcessResult,
  type AttachmentRecord,
  type AttachmentWarning,
  type CliModule,
  type OneOutcome,
  type PlanOutcome,
  type UploadPlan,
  type UploaderState,
  type UploadStep,
} from './attachTypes.js';

/**
 * The attachment pipeline of one producer. `process()` runs per attempt and never rejects;
 * `finish()` is `onEnd`'s drain and never rejects either. A merge-replay or a writer-less
 * reporter never constructs one.
 */
export class AttachmentPipelineCore {
  private readonly guard: PathGuard;
  private readonly drain: UploadDrain;
  private readonly deadlineMs: number;
  /** The allow-list, handed to the CLI's own guard unchanged (the two must agree). */
  private readonly roots: readonly string[];
  private readonly cwd: string;
  private readonly env: Readonly<Record<string, string | undefined>>;
  private readonly importCli: () => Promise<CliModule>;
  /** The memoised CLI module load; `undefined` until the first uploadable attachment. */
  private cliModule: Promise<CliModule> | undefined;
  private uploaderState: Promise<UploaderState> | undefined;
  /** The same state, once resolved: what {@link noteRunWarning} reads without awaiting. */
  private resolvedState: UploaderState | undefined;
  /** Attempts (`process()` calls) whose uploads have not all settled yet. */
  private readonly attempts = new Set<Promise<AttachmentProcessResult>>();
  /** Attempt sequence numbers with at least one deadline outcome (for the onEnd warning). */
  private readonly deadlineAttempts = new Set<number>();
  private attemptSeq = 0;
  private considered = 0;
  private limitWarned = false;

  constructor(
    options: AttachmentPipelineOptions & { readonly importCli: () => Promise<CliModule> },
  ) {
    this.roots = [...options.roots];
    this.cwd = options.cwd;
    this.guard = createPathGuard({ roots: this.roots, cwd: this.cwd });
    this.deadlineMs = options.deadlineMs ?? DEFAULT_DRAIN_DEADLINE_MS;
    this.env = options.env ?? process.env;
    this.importCli = options.importCli;
    const drainOptions: UploadDrainOptions = {
      deadlineMs: this.deadlineMs,
      ...(options.concurrency === undefined ? {} : { concurrency: options.concurrency }),
      ...(options.timers === undefined ? {} : { timers: options.timers }),
      ...(options.now === undefined ? {} : { now: options.now }),
    };
    this.drain = new UploadDrain(drainOptions);
  }

  /** True while an attempt's uploads have not all settled (the caller must call `finish`). */
  get hasPending(): boolean {
    return this.attempts.size > 0;
  }

  /** The drain deadline this pipeline was built with. */
  get drainDeadlineMs(): number {
    return this.deadlineMs;
  }

  /**
   * Maps, guards, caps and uploads one attempt's attachments. Never rejects: every failure is
   * a record plus a warning, so the caller can always emit the test result.
   *
   * The upload JOBS of one attachment start in the order they were queued (`UploadDrain` keeps
   * at most two in flight). Their PUTs may still interleave: a job's own first awaits (the path
   * guard, the uploader load) decide when its request actually goes out, so request order is
   * not part of this contract. The returned promise settles when the attempt's jobs settled or
   * the drain deadline abandoned them.
   */
  process(input: AttachmentProcessInput): Promise<AttachmentProcessResult> {
    const attemptId = ++this.attemptSeq;
    const promise = this.runAttempt(attemptId, input);
    this.attempts.add(promise);
    const settled = (): void => {
      this.attempts.delete(promise);
    };
    void promise.then(settled, settled);
    return promise;
  }

  /**
   * Starts the drain deadline and waits for every in-flight attempt, including the ones still
   * resolving an attachment path or a lazily loaded uploader. Resolves at the deadline even if
   * an upload hangs; never rejects.
   */
  async finish(): Promise<AttachmentFinishResult> {
    const drained = await this.drain.finish();
    while (this.attempts.size > 0) {
      await Promise.all([...this.attempts]);
    }
    return {
      timedOut: drained.timedOut,
      abandoned: drained.abandoned,
      deadlineAttempts: this.deadlineAttempts.size,
    };
  }

  private async runAttempt(
    attemptId: number,
    input: AttachmentProcessInput,
  ): Promise<AttachmentProcessResult> {
    const records: AttachmentRecord[] = [];
    const warnings: AttachmentWarning[] = [];
    const artifactIds: string[] = [];
    try {
      // The queue is filled SYNCHRONOUSLY here, before the first await: `onEnd` may run
      // before an attempt's guard or uploader load finished, and a job submitted after
      // `UploadDrain.finish()` resolved would be abandoned at once. Enqueuing up front makes
      // the drain deadline cover the whole attempt (map/guard/cap/upload), not just the PUT.
      const slots: Array<OneOutcome | undefined> = [];
      const pending: Array<Promise<void>> = [];
      for (const attachment of input.attachments) {
        if (
          slots.length >= MAX_ATTACHMENTS_PER_TEST ||
          this.considered >= MAX_ATTACHMENTS_PER_RUN
        ) {
          this.noteLimit(warnings, input.attachments.length - slots.length);
          break;
        }
        this.considered += 1;
        const slot = slots.length;
        const planned = this.plan(attachment, input);
        if (planned.kind === 'done') {
          slots.push(planned.outcome);
          continue;
        }
        slots.push(undefined);
        const plan = planned.plan;
        const submission = this.drain.submit((context) => this.uploadJob(context, plan, input));
        pending.push(
          submission.then((outcome) => {
            slots[slot] = this.settle(attemptId, plan, outcome);
          }),
        );
      }
      await Promise.all(pending);
      // Once per run, and never swallowed by a job the deadline abandoned: the warning belongs
      // to the ATTEMPT that observed the uploader state, so a job whose step was discarded can
      // no longer consume it.
      this.noteRunWarning(warnings);
      for (const slot of slots) {
        if (slot === undefined) continue;
        records.push(slot.record);
        warnings.push(...slot.warnings);
        if (slot.artifactId !== undefined) artifactIds.push(slot.artifactId);
      }
    } catch {
      // `plan()` and `settle()` already turn every documented failure into a record; this is
      // the belt-and-braces path (an unexpected bug) and still keeps the test result.
      warnings.push({
        code: 'attachments_unavailable',
        message: 'the attachment pipeline failed; the remaining attachments were skipped',
      });
    }
    return { artifactIds, records, warnings };
  }

  /**
   * The synchronous half of one attachment: map it, apply the sensitivity rule and, when it
   * is not uploadable at all, answer the record directly. Everything async (guard, uploader
   * load, cap, upload) travels through the drain so the deadline covers it.
   */
  private plan(attachment: PlaywrightAttachment, input: AttachmentProcessInput): PlanOutcome {
    const displayName = shownName(attachment);
    const mapped = mapAttachment(attachment, { sensitive: input.sensitiveAnnotated });
    if (!mapped.ok) {
      return {
        kind: 'done',
        outcome: {
          record: {
            name: displayName,
            sensitive: input.sensitiveAnnotated,
            outcome: 'skipped',
            code: mapped.code,
          },
          warnings: [{ code: mapped.code, message: mapped.message, path: displayName }],
        },
      };
    }
    const candidate = mapped.attachment;
    // The annotation opts in, and a trace is sensitive by default. Never downgraded — the
    // record and the warning below are the only carriers, because the wire has no sensitivity
    // member yet (contracts/cli.md `createArtifactUploadUrl`).
    const sensitive = candidate.sensitive || candidate.kind === 'trace';
    const warnings: AttachmentWarning[] = [];
    if (candidate.sensitive && candidate.kind !== 'trace') {
      warnings.push({
        code: 'attachment_sensitivity_not_carried',
        message:
          `attachment '${candidate.name}' (${candidate.kind}): the sensitivity opt-in is ` +
          'not carried on the wire yet; the attachment uploads under the project policy',
        path: candidate.name,
      });
    }
    return { kind: 'upload', plan: { attachment: candidate, sensitive, warnings } };
  }

  /**
   * The asynchronous half, run by the drain: guard → uploader → cap → upload. Every documented
   * failure is a {@link UploadStep}, never a rejection, so the drain's own `failed` outcome
   * stays reserved for a bug.
   */
  private async uploadJob(
    context: DrainJobContext,
    plan: UploadPlan,
    input: AttachmentProcessInput,
  ): Promise<UploadStep> {
    const warnings = [...plan.warnings];
    const candidate = plan.attachment;
    try {
      // The allow-list first: no CLI load and no request for a path outside it. This await is
      // also why two started jobs can issue their PUTs in either order: the guard (and the
      // uploader load below) resolves per file, so `UploadDrain`'s FIFO is a START order only.
      const verdict = await this.guard.check(candidate.path);
      if (!verdict.ok) {
        return { state: 'skipped', code: verdict.code, message: verdict.message, warnings };
      }
      const state = await this.uploader();
      const uploader = state.uploader;
      if (uploader === null) {
        // The run-level warning (`attachments_unavailable`/`attachments_unconfigured`) is
        // claimed once per run by the ATTEMPT that observed this state, in `runAttempt` — not
        // here: a job abandoned at the deadline must not consume a warning no envelope then
        // carries.
        return { state: 'unconfigured', warnings };
      }
      // The cap is checked on the guard's own size, before the CLI reads a byte.
      if (isOverCap(candidate.kind, verdict.size, uploader.kindMaxBytes)) {
        return {
          state: 'skipped',
          code: 'attachment_over_cap',
          message:
            `attachment '${candidate.name}' (${candidate.kind}) is ${String(verdict.size)} ` +
            'bytes, over the per-kind cap, skipped',
          warnings,
        };
      }
      return await this.uploadArtifact(
        uploader,
        candidate,
        verdict.realPath,
        input,
        context,
        warnings,
      );
    } catch (error) {
      // The CLI error's CODE only. Its message can embed a presigned URL, a token echo or a
      // path, so it is never forwarded.
      const code = errorCode(error);
      return {
        state: 'failed',
        code,
        message: `attachment '${candidate.name}' (${candidate.kind}) upload failed (${code})`,
        warnings,
      };
    }
  }

  /**
   * The upload itself: the drain deadline, the facade call and the artifact-id check. Awaited
   * inside `uploadJob`'s `try`, so a rejection is answered as `failed` exactly as before.
   */
  private async uploadArtifact(
    uploader: AttachmentUploader,
    candidate: MappedAttachment,
    realPath: string,
    input: AttachmentProcessInput,
    context: DrainJobContext,
    warnings: AttachmentWarning[],
  ): Promise<UploadStep> {
    const remaining = context.remainingMs();
    const deadlineMs = Number.isFinite(remaining)
      ? Math.min(this.deadlineMs, remaining)
      : this.deadlineMs;
    // The drain's own signal travels with the upload: the deadline stops the WAIT, so the
    // facade reads it at ITS checkpoints (before the file is opened, after the digest, after
    // the bearer and before the first mint) and answers `upload_aborted`. A PUT already in
    // flight runs to its own deadline — abandoned at the next checkpoint, never cancelled
    // mid-PUT (`uploadFile` takes no signal).
    const uploaded = await uploader.upload(
      {
        runId: input.runId,
        producerId: input.producerId,
        executionKey: input.executionKey,
        attemptNo: input.attemptNo,
        kind: candidate.kind,
        mediaType: candidate.mediaType,
        file: realPath,
      },
      { deadlineMs, signal: context.signal },
    );
    if (!UUID_PATTERN.test(uploaded.artifactId)) {
      return {
        state: 'failed',
        code: 'attachment_upload_failed',
        message:
          `attachment '${candidate.name}' (${candidate.kind}) upload returned no usable ` +
          'artifact id',
        warnings,
      };
    }
    return { state: 'uploaded', artifactId: uploaded.artifactId, warnings };
  }

  /** Turns one drain outcome into the attachment's record (and its warning, if any). */
  private settle(attemptId: number, plan: UploadPlan, outcome: JobOutcome<UploadStep>): OneOutcome {
    const candidate = plan.attachment;
    const base = {
      name: candidate.name,
      kind: candidate.kind,
      sensitive: plan.sensitive,
    } as const;
    if (outcome.status === 'deadline') {
      this.deadlineAttempts.add(attemptId);
      return {
        record: { ...base, outcome: 'failed', code: 'attachment_drain_deadline' },
        warnings: [...plan.warnings],
      };
    }
    if (outcome.status === 'failed') {
      // A job that threw before answering its step: a bug, reported without its message.
      const code = errorCode(outcome.error);
      return {
        record: { ...base, outcome: 'failed', code },
        warnings: [
          ...plan.warnings,
          {
            code: 'attachment_upload_failed',
            message: `attachment '${candidate.name}' (${candidate.kind}) upload failed (${code})`,
            path: candidate.name,
          },
        ],
      };
    }
    const step = outcome.value;
    if (step.state === 'uploaded') {
      return {
        record: { ...base, outcome: 'uploaded', artifact_id: step.artifactId },
        artifactId: step.artifactId,
        warnings: step.warnings,
      };
    }
    if (step.state === 'unconfigured') {
      return {
        record: { ...base, outcome: 'skipped', code: 'attachments_unconfigured' },
        warnings: step.warnings,
      };
    }
    if (step.state === 'skipped') {
      return {
        record: { ...base, outcome: 'skipped', code: step.code },
        warnings: [
          ...step.warnings,
          { code: step.code, message: step.message, path: candidate.name },
        ],
      };
    }
    return {
      record: { ...base, outcome: 'failed', code: step.code },
      warnings: [
        ...step.warnings,
        { code: 'attachment_upload_failed', message: step.message, path: candidate.name },
      ],
    };
  }

  /**
   * The one lazy `@evidgate/cli` load of a run, memoised. A failed import or an unresolvable
   * configuration answers a NULL uploader plus the ONE warning the first attempt emits: the
   * run keeps emitting every test result and stays spool-only.
   */
  private uploader(): Promise<UploaderState> {
    this.uploaderState ??= this.loadUploader().then((state) => {
      // Published synchronously as soon as it resolves, so an attempt that is aggregating its
      // warnings right then can claim the run-level warning without awaiting the load again.
      this.resolvedState = state;
      return state;
    });
    return this.uploaderState;
  }

  /**
   * Carries the ONE run-level warning (`attachments_unavailable`/`attachments_unconfigured`) on
   * the first attempt that observed the state, whatever became of its jobs. Called
   * synchronously after an attempt's jobs settled; a state that never resolved has no warning
   * to carry yet — the run never learned of a problem.
   */
  private noteRunWarning(warnings: AttachmentWarning[]): void {
    const state = this.resolvedState;
    if (state === undefined || state.emitted || state.warning === undefined) {
      return;
    }
    state.emitted = true;
    warnings.push(state.warning);
  }

  private async loadUploader(): Promise<UploaderState> {
    let cli: CliModule;
    try {
      this.cliModule ??= this.importCli();
      cli = await this.cliModule;
    } catch {
      // A broken install is not a configuration problem: the import's own error text (which
      // names module paths) is never forwarded.
      return {
        uploader: null,
        warning: {
          code: 'attachments_unavailable',
          message:
            'the attachment uploader is unavailable (@evidgate/cli could not be loaded); ' +
            'every attachment is skipped and the test results are kept',
        },
        emitted: false,
      };
    }
    try {
      const uploader = cli.createAttachmentUploader({
        env: this.env,
        dirs: this.roots,
        cwd: this.cwd,
      });
      return { uploader, warning: undefined, emitted: false };
    } catch (error) {
      // The CLI's `missing_config`/`invalid_config` disables uploads for the whole run
      // (one warning), and the spool-only offline mode is unchanged. The code is forwarded,
      // the message is not.
      return {
        uploader: null,
        warning: {
          code: 'attachments_unconfigured',
          message:
            `attachment uploads are unconfigured (${errorCode(error)}); every attachment is ` +
            'skipped and the run stays spool-only',
        },
        emitted: false,
      };
    }
  }

  /** ONE `attachment_limit` warning per run: the overflow is skipped, never uploaded. */
  private noteLimit(warnings: AttachmentWarning[], skipped: number): void {
    if (this.limitWarned) return;
    this.limitWarned = true;
    warnings.push({
      code: 'attachment_limit',
      message:
        `attachment limit reached (at most ${String(MAX_ATTACHMENTS_PER_TEST)} per test and ` +
        `${String(MAX_ATTACHMENTS_PER_RUN)} per run are considered); further attachments are ` +
        'skipped',
      count: skipped,
    });
  }
}

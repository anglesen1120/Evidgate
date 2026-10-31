// The attachment pipeline between `result.attachments` and the `artifact_ids` of `test.finished`.
//
// Why a pipeline object instead of code inside `onTestEnd`: the callback must stay cheap and
// synchronous for the common case (no attachments), and the upload work must survive past the
// callback — Playwright gives the reporter no chance to await per-test work, only `onEnd`. So
// this object owns the three pieces the mapping, guard and drain modules provide and the
// reporter does not have to reason about any of them:
// - `attachMap.mapAttachment` (kind, media type, sensitive flag),
// - `attachPaths.createPathGuard` (allow-listed roots, symlink and traversal refusal, size) and
//   `attachMap.isOverCap` (per-kind cap, checked BEFORE a byte is read),
// - `drain.UploadDrain` (bounded concurrency, one deadline).
//
// Dependency direction: this file is the ONE place in the reporter that loads `@evidgate/cli` at
// runtime, and only lazily — a run without an uploadable attachment never pays for the import,
// and a failed import (a broken install) degrades to one `attachments_unavailable` warning per
// run while every test result is still emitted. A missing CLI configuration
// (`missing_config`/`invalid_config` from `createAttachmentUploader`) is
// `attachments_unconfigured`, once per run; the spool-only offline mode keeps working unchanged.
//
// Order of operations per attachment is what makes the refusals free: map (pure) → path guard
// (no CLI, no read) → uploader config (memoised) → per-kind cap → `drain.submit(upload)`. A
// refused attachment therefore costs no request at all.
//
// Secret hygiene: an upload failure becomes a record plus a warning that name only the file's
// BASE name, the kind and the CLI's own error `code`. The error message is never forwarded — it
// can embed a presigned URL or a path — and the artifact id is only taken from a completed
// upload whose value is a UUID. The token and the presigned URL live inside the CLI's uploader
// and never reach an envelope.
//
// Bounds: at most {@link MAX_ATTACHMENTS_PER_TEST} attachments of one test and
// {@link MAX_ATTACHMENTS_PER_RUN} per run are considered; the overflow is skipped with ONE
// `attachment_limit` warning per run, so a suite that attaches a thousand screenshots cannot
// flood the capped warning channel or the spool.
import type { AttachmentUploader } from '@evidgate/cli';

import {
  isOverCap,
  mapAttachment,
  type AttachmentKind,
  type MappedAttachment,
  type PlaywrightAttachment,
  type SkipCode,
} from './attachMap.js';
import { createPathGuard, type PathGuard } from './attachPaths.js';
import {
  DEFAULT_DRAIN_DEADLINE_MS,
  UploadDrain,
  type DrainJobContext,
  type DrainTimers,
  type JobOutcome,
  type UploadDrainOptions,
} from './drain.js';

/**
 * The `@evidgate/cli` module surface, as the ONE lazy dynamic import loads it. A named alias,
 * so the dependency stays visible at the top of the file and the seam's signature stays
 * readable.
 */
export type CliModule = typeof import('@evidgate/cli');

/** The most attachments of one test this pipeline considers (the overflow is skipped). */
export const MAX_ATTACHMENTS_PER_TEST = 50;

/** The most attachments of one run this pipeline considers. */
export const MAX_ATTACHMENTS_PER_RUN = 1000;

/** The most UTF-16 units of a base name a record, warning or message carries. */
const NAME_MAX = 120;

/** Control and format characters, escaped rather than removed (`status.ts` `printable`). */
const UNSAFE_CHARS = /[\p{Cc}\p{Cf}]/gu;

/** The escapes a reader can undo; every other unsafe character becomes `\u{<hex>}`. */
const NAMED_ESCAPES: Readonly<Record<string, string>> = {
  '\n': '\\n',
  '\r': '\\r',
  '\t': '\\t',
};

/** A UUID in any case; the artifact id from a completed upload must be one of these. */
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;

/** A CLI error code is a bare token; anything else is reported as `unknown`. */
const CODE_PATTERN = /^[A-Za-z0-9_.-]{1,64}$/u;

/**
 * The warning codes this pipeline and its reporter wiring emit, on top of the skip codes
 * `attachMap`/`attachPaths` decide (`attachment_unmapped_type`, `attachment_over_cap`, …).
 *
 * `attachments_unavailable` is one per run: the lazy `@evidgate/cli` import failed.
 * `attachments_unconfigured` is one per run: the CLI's own config does not resolve, so
 * uploads are disabled and the run stays spool-only. `attachment_no_execution_key` is one per
 * test: Playwright reported attachments but the reporter has no planned execution key.
 * `attachment_upload_failed` names the CLI/server code only; `attachment_drain_deadline` is
 * one per run at `onEnd`; `attachment_limit` is one per run. `shards_write_failed` and
 * `shards_floor_ahead` are the writer's own once-per-run notices, handed over from the spool
 * writer.
 */
export type AttachmentWarningCode =
  | 'attachments_unavailable'
  | 'attachments_unconfigured'
  | 'attachment_no_execution_key'
  | 'attachment_sensitivity_not_carried'
  | 'attachment_upload_failed'
  | 'attachment_drain_deadline'
  | 'attachment_limit'
  | 'shards_write_failed'
  | 'shards_floor_ahead';

/** One `run.log` warning this pipeline (or the reporter's attachment wiring) emits. */
export interface AttachmentWarning {
  code: AttachmentWarningCode | SkipCode;
  message: string;
  /** The attachment's base name only — never a directory. */
  path?: string;
  count?: number;
}

/** One attachment as the run evidence records it: base names only, no path, no URL. */
export interface AttachmentRecord {
  /** The file's base name, escaped and capped for an event line. */
  name: string;
  kind?: AttachmentKind;
  /** `true` when the case opted in (`evidgate.sensitive`) or the kind is sensitive by default. */
  sensitive: boolean;
  outcome: 'uploaded' | 'skipped' | 'failed';
  /** The skip or failure code (`attachment_over_cap`, `attachment_upload_failed`, …). */
  code?: string;
  /** Set only for a completed upload whose id is a UUID. */
  artifact_id?: string;
}

/** One attempt's attachments, as the reporter hands them over. */
export interface AttachmentProcessInput {
  readonly attachments: readonly PlaywrightAttachment[];
  /** The suite or case opted in through the `evidgate.sensitive` annotation. */
  readonly sensitiveAnnotated: boolean;
  readonly runId: string;
  readonly producerId: string;
  readonly executionKey: string;
  readonly attemptNo: number;
}

/** What one attempt produced: the ids to reference, the records to attach, the warnings. */
export interface AttachmentProcessResult {
  /** Ids of the COMPLETED uploads, in attachment order, deduplicated by the caller's use. */
  artifactIds: string[];
  records: AttachmentRecord[];
  warnings: AttachmentWarning[];
}

/** What {@link AttachmentPipeline.finish} reports after the drain deadline. */
export interface AttachmentFinishResult {
  /** True when the deadline fired before every upload settled. */
  timedOut: boolean;
  /** Jobs the deadline had to abandon. */
  abandoned: number;
  /** Attempts (`process()` calls) with at least one attachment abandoned at the deadline. */
  deadlineAttempts: number;
}

export interface AttachmentPipelineOptions {
  /** The allow-listed roots (`defaultAttachmentRoots`). */
  readonly roots: readonly string[];
  /** The directory relative attachment paths and roots resolve against. */
  readonly cwd: string;
  /** The `onEnd` drain deadline; default {@link DEFAULT_DRAIN_DEADLINE_MS}. */
  readonly deadlineMs?: number;
  /** The CLI's `EVIDGATE_*` surface, never the ambient process env read directly. */
  readonly env?: Readonly<Record<string, string | undefined>>;
  /**
   * The single runtime load of `@evidgate/cli`. Memoised, so a run imports the CLI at most
   * once; a spec injects a fake module here.
   */
  readonly importCli?: () => Promise<CliModule>;
  /** Requested upload parallelism; `UploadDrain` clamps it to at most 2 per shard. */
  readonly concurrency?: number;
  /** Timer/clock seams for the drain (a spec advances a virtual clock). */
  readonly timers?: DrainTimers;
  readonly now?: () => number;
}

/** The uploader load's outcome: the uploader, or the state that makes every upload a skip. */
interface UploaderState {
  readonly uploader: AttachmentUploader | null;
  readonly warning: AttachmentWarning | undefined;
  /** Set by the first attempt that CARRIES the warning, so it is emitted once per run. */
  emitted: boolean;
}

/** One attachment's contribution, computed independently of the others of its attempt. */
interface OneOutcome {
  record: AttachmentRecord;
  artifactId?: string;
  warnings: AttachmentWarning[];
}

/** An attachment that survives mapping: everything the queued job needs to read. */
interface UploadPlan {
  readonly attachment: MappedAttachment;
  /** The final sensitivity flag: the annotation, or the kind's default. */
  readonly sensitive: boolean;
  /** The mapping-time warnings the record's warning carries. */
  readonly warnings: readonly AttachmentWarning[];
}

/** Where one attachment stops: a record now (a skip) or a job on the drain. */
type PlanOutcome =
  | { readonly kind: 'done'; readonly outcome: OneOutcome }
  | { readonly kind: 'upload'; readonly plan: UploadPlan };

/** What the queued job answers; never an exception for a documented refusal. */
type UploadStep =
  | { readonly state: 'uploaded'; readonly artifactId: string; warnings: AttachmentWarning[] }
  | {
      readonly state: 'skipped';
      readonly code: SkipCode;
      readonly message: string;
      warnings: AttachmentWarning[];
    }
  | { readonly state: 'unconfigured'; warnings: AttachmentWarning[] }
  | {
      readonly state: 'failed';
      readonly code: string;
      readonly message: string;
      warnings: AttachmentWarning[];
    };

/**
 * The attachment pipeline of one producer. `process()` runs per attempt and never rejects;
 * `finish()` is `onEnd`'s drain and never rejects either. A merge-replay or a writer-less
 * reporter never constructs one.
 */
export class AttachmentPipeline {
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

  constructor(options: AttachmentPipelineOptions) {
    this.roots = [...options.roots];
    this.cwd = options.cwd;
    this.guard = createPathGuard({ roots: this.roots, cwd: this.cwd });
    this.deadlineMs = options.deadlineMs ?? DEFAULT_DRAIN_DEADLINE_MS;
    this.env = options.env ?? process.env;
    this.importCli = options.importCli ?? (() => import('@evidgate/cli'));
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
          file: verdict.realPath,
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

/**
 * The CLI's error `code` as a bare token, or `unknown`. The MESSAGE is deliberately not
 * forwarded: it can embed a presigned URL, a token echo or a filesystem path.
 */
function errorCode(error: unknown): string {
  if (typeof error !== 'object' || error === null || !('code' in error)) return 'unknown';
  const code = error.code;
  return typeof code === 'string' && CODE_PATTERN.test(code) ? code : 'unknown';
}

/** The name an attachment is known by: base name only, escaped, capped. */
function shownName(attachment: PlaywrightAttachment): string {
  const path = typeof attachment.path === 'string' ? attachment.path : '';
  const raw = path.length > 0 ? baseNameOf(path) : String(attachment.name ?? '');
  return cut(escapeControls(raw), NAME_MAX);
}

/** The last segment of a path that may use either separator; `''` when there is none. */
function baseNameOf(path: string): string {
  const normalised = path.replaceAll('\\', '/');
  const index = normalised.lastIndexOf('/');
  return index >= 0 ? normalised.slice(index + 1) : normalised;
}

/**
 * Every control or format character as visible text (the same rule as
 * `attachMap.ts`/`attachPaths.ts`: an escape keeps tampering visible instead of hiding it).
 */
function escapeControls(value: string): string {
  return value.replace(UNSAFE_CHARS, (character) => {
    const named = NAMED_ESCAPES[character];
    if (named !== undefined) return named;
    const codePoint = character.codePointAt(0) ?? 0;
    return codePoint <= 0xffff
      ? `\\u${codePoint.toString(16).padStart(4, '0')}`
      : `\\u{${codePoint.toString(16)}}`;
  });
}

/** At most `max` UTF-16 units, never ending on a lone high surrogate. */
function cut(value: string, max: number): string {
  if (value.length <= max) return value;
  let end = max;
  const last = value.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) end -= 1;
  return value.slice(0, end);
}

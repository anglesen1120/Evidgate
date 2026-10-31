// The shapes the attachment pipeline exchanges with the reporter: the CLI module alias, the
// record and warning types of one attempt, the per-run bounds and the internal steps between
// mapping, the drain and the upload. Types only — the behaviour is in `attachPipeline`.
import type { AttachmentUploader } from '@evidgate/cli';

import type {
  AttachmentKind,
  MappedAttachment,
  PlaywrightAttachment,
  SkipCode,
} from './attachMap.js';
import type { DrainTimers } from './drain.js';

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
export interface UploaderState {
  readonly uploader: AttachmentUploader | null;
  readonly warning: AttachmentWarning | undefined;
  /** Set by the first attempt that CARRIES the warning, so it is emitted once per run. */
  emitted: boolean;
}

/** One attachment's contribution, computed independently of the others of its attempt. */
export interface OneOutcome {
  record: AttachmentRecord;
  artifactId?: string;
  warnings: AttachmentWarning[];
}

/** An attachment that survives mapping: everything the queued job needs to read. */
export interface UploadPlan {
  readonly attachment: MappedAttachment;
  /** The final sensitivity flag: the annotation, or the kind's default. */
  readonly sensitive: boolean;
  /** The mapping-time warnings the record's warning carries. */
  readonly warnings: readonly AttachmentWarning[];
}

/** Where one attachment stops: a record now (a skip) or a job on the drain. */
export type PlanOutcome =
  | { readonly kind: 'done'; readonly outcome: OneOutcome }
  | { readonly kind: 'upload'; readonly plan: UploadPlan };

/** What the queued job answers; never an exception for a documented refusal. */
export type UploadStep =
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

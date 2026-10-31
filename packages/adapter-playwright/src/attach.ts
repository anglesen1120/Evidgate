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
//
// This module is the public surface: the pipeline itself is `attachPipeline.ts`, the shapes are
// `attachTypes.ts` and the record-name hygiene is `attachNames.ts`.
import { AttachmentPipelineCore } from './attachPipeline.js';
import type { AttachmentPipelineOptions, CliModule } from './attachTypes.js';

/** The pipeline with the one runtime load of the CLI wired in as its default. */
export class AttachmentPipeline extends AttachmentPipelineCore {
  constructor(options: AttachmentPipelineOptions) {
    super({
      ...options,
      importCli: options.importCli ?? ((): Promise<CliModule> => import('@evidgate/cli')),
    });
  }
}

export {
  MAX_ATTACHMENTS_PER_RUN,
  MAX_ATTACHMENTS_PER_TEST,
  type AttachmentFinishResult,
  type AttachmentPipelineOptions,
  type AttachmentProcessInput,
  type AttachmentProcessResult,
  type AttachmentRecord,
  type AttachmentWarning,
  type AttachmentWarningCode,
  type CliModule,
} from './attachTypes.js';

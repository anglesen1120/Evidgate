// Attachment hold and drain. Uploads start as soon as a test ends, but the attempt's batch is
// held until they settle, so a `test.finished` never references an artifact that is still in
// flight. Only the append is serialised: that keeps held batches in test-end order and lets
// the writer assign contiguous sequences at append time. An attempt without attachments is
// appended immediately, so it may precede an earlier test whose uploads are still running.
import type { FullConfig, TestCase, TestResult } from '@playwright/test/reporter';

import { AttachmentPipeline, type CliModule } from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import { defaultAttachmentRoots } from './attachPaths.js';
import type { AttemptDraft } from './reporterAttempt.js';
import { toError } from './reporterFailure.js';
import type { ReporterWarning } from './reporterWarnings.js';
import type { SpoolWriter } from './spool.js';

export interface AttachmentPipelineSetup {
  /** Extra allow-listed roots, after the Playwright output dirs and `test-results`. */
  readonly attachmentDirs: readonly string[];
  readonly deadlineMs: number;
  readonly importCli: (() => Promise<CliModule>) | undefined;
}

/**
 * Builds the producer's pipeline with the allow-list the CLI's own upload guard receives:
 * every Playwright `outputDir`, `<rootDir>/test-results` and the configured extra dirs. In a
 * `merge-reports` replay or a refused `onBegin` it is never built, so attachments are never
 * touched there.
 */
export function buildAttachmentPipeline(
  setup: AttachmentPipelineSetup,
  config: FullConfig | undefined,
): AttachmentPipeline {
  const rootDir = config?.rootDir ?? process.cwd();
  return new AttachmentPipeline({
    roots: defaultAttachmentRoots({
      outputDirs: (config?.projects ?? []).map((project) => project.outputDir),
      rootDir,
      extraDirs: setup.attachmentDirs,
    }),
    cwd: rootDir,
    deadlineMs: setup.deadlineMs,
    env: process.env,
    ...(setup.importCli === undefined ? {} : { importCli: setup.importCli }),
  });
}

/**
 * The suite (test) or the case (result) opted in through the `evidgate.sensitive`
 * annotation. Presence is the opt-in whatever value it carries, and the flag is never
 * downgraded.
 */
export function hasSensitiveAnnotation(test: TestCase, result: TestResult): boolean {
  for (const annotation of [...(test.annotations ?? []), ...(result.annotations ?? [])]) {
    if (annotation.type === 'evidgate.sensitive') return true;
  }
  return false;
}

export interface AttachmentCoordinatorDeps {
  readonly runId: string;
  /** The `onEnd` drain deadline, named in the abandonment warning. */
  readonly deadlineMs: number;
  readonly producerId: () => string | undefined;
  readonly pipeline: () => AttachmentPipeline | undefined;
  readonly appendAttempt: (writer: SpoolWriter, attempt: AttemptDraft) => void;
  readonly warn: (warning: ReporterWarning) => void;
  readonly emitNotices: () => void;
  readonly bufferFailure: (error: unknown) => void;
}

export class AttachmentCoordinator {
  private chain: Promise<void> = Promise.resolve();
  private queued = 0;

  constructor(private readonly deps: AttachmentCoordinatorDeps) {}

  /** True when `onEnd` may run synchronously: no attempt queued and no upload pending. */
  get idle(): boolean {
    return this.queued === 0 && !(this.deps.pipeline()?.hasPending ?? false);
  }

  /**
   * Starts the attempt's uploads now and holds its drafts until they settled. The appends
   * are serialised, so no held batch can overtake another held batch.
   */
  hold(
    writer: SpoolWriter,
    attempt: AttemptDraft,
    attachments: readonly PlaywrightAttachment[],
    sensitiveAnnotated: boolean,
  ): void {
    const pipeline = this.deps.pipeline();
    const producerId = this.deps.producerId();
    if (pipeline === undefined || producerId === undefined) return;
    const work = pipeline.process({
      attachments,
      sensitiveAnnotated,
      runId: this.deps.runId,
      producerId,
      executionKey: attempt.identity.execution_key,
      attemptNo: attempt.attemptNo,
    });
    this.queued += 1;
    this.chain = this.chain.then(async () => {
      try {
        const outcome = await work;
        for (const warning of outcome.warnings) this.deps.warn(warning);
        this.deps.appendAttempt(writer, {
          ...attempt,
          artifactIds: outcome.artifactIds,
          records: outcome.records,
        });
        this.deps.emitNotices();
      } catch (error) {
        // Buffered like every other callback failure, so `onEnd` fails the run instead of a
        // rejection escaping a callback Playwright does not await.
        this.deps.bufferFailure(toError(error));
      } finally {
        this.queued -= 1;
      }
    });
  }

  /**
   * Waits for every upload under the deadline, then appends the held batches. The deadline
   * stops the WAIT only: a PUT that already started runs to its own deadline and is
   * abandoned at its next checkpoint, never cancelled mid-request. Held batches land before
   * the end markers, because a late test after `run_end` would read as a post-run event.
   */
  async drain(): Promise<void> {
    const pipeline = this.deps.pipeline();
    const drained =
      pipeline === undefined
        ? { timedOut: false, abandoned: 0, deadlineAttempts: 0 }
        : await pipeline.finish();
    await this.chain;
    if (drained.deadlineAttempts > 0 || drained.abandoned > 0) {
      this.deps.warn({
        code: 'attachment_drain_deadline',
        message:
          `the attachment drain deadline (${String(this.deps.deadlineMs)} ms) ` +
          `abandoned ${String(drained.deadlineAttempts)} attempt(s); an abandoned ` +
          'attachment has no artifact_id in its test.finished envelope',
        count: drained.deadlineAttempts,
      });
    }
    this.deps.emitNotices();
  }
}

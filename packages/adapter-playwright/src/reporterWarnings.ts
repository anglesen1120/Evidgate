// The warning channel: one capped stream of `run.log` envelopes plus the counts `onEnd`
// reports. The cap exists because a hostile suite (thousands of conflicting annotations)
// must not be able to flood the spool, and the overflow is reported as a count instead of
// being dropped silently.
import type { AttachmentWarning } from './attach.js';
import type { WarningPayload } from './identity.js';
import { MAX_WARNING_ENVELOPES } from './reporterOptions.js';
import type { SpoolWriter } from './spool.js';

/** A warning payload this adapter emits; identity and attachment warnings share one shape. */
export type ReporterWarning = WarningPayload | AttachmentWarning;

export class ReporterWarnings {
  private emitted = 0;
  private suppressedCount = 0;
  private unavailableWarned = false;

  /** `emit` appends one uncapped warning envelope through the active writer. */
  constructor(private readonly emit: (warning: ReporterWarning) => void) {}

  /** Emits one warning envelope, up to the cap; the overflow is counted, not emitted. */
  warn(warning: ReporterWarning): void {
    if (this.emitted >= MAX_WARNING_ENVELOPES) {
      this.suppressedCount += 1;
      return;
    }
    this.emitted += 1;
    this.emit(warning);
  }

  /** Warnings lost to a cap: the envelope cap here, or the sidecar loader's own. */
  get suppressed(): number {
    return this.suppressedCount;
  }

  noteSuppressed(count: number): void {
    this.suppressedCount += count;
  }

  /** ONE `attachments_unavailable` warning per run: no pipeline, so nothing is uploaded. */
  warnUnavailableOnce(): void {
    if (this.unavailableWarned) return;
    this.unavailableWarned = true;
    this.warn({
      code: 'attachments_unavailable',
      message: 'the attachment pipeline is unavailable for this run; attachments are skipped',
    });
  }

  /**
   * Turns the writer's once-per-run notices into `run.log` warnings. `takeNotices()` drains
   * what it returns and the writer queues each code once per run, so calling this after
   * every flush that can rotate cannot duplicate a warning.
   */
  emitWriterNotices(writer: SpoolWriter | undefined): void {
    if (writer === undefined) return;
    for (const notice of writer.takeNotices()) {
      this.warn({ code: notice.code, message: notice.message });
    }
  }
}

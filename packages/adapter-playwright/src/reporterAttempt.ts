// One attempt's batch: its `test.finished`, the status warning, every step and one flush.
// The synchronous path and the attachment-held path share this appender so the two can never
// drift; the spool writer assigns each `sequence` when the envelope is appended, in batch
// order, which is why nothing here builds a draft ahead of time.
import type { TestCase, TestResult } from '@playwright/test/reporter';

import type { AttachmentRecord } from './attach.js';
import { locationOf, type Identity } from './identity.js';
import type { DraftBuilder } from './reporterDraft.js';
import type { TestExtBuilder } from './reporterExt.js';
import { durationField, walkSteps } from './reporterSteps.js';
import type { ReporterWarning } from './reporterWarnings.js';
import type { SpoolWriter } from './spool.js';
import { printable, type StatusMapping } from './status.js';

/** Everything `test.finished` and its steps need, held until the attempt's uploads settled. */
export interface AttemptDraft {
  test: TestCase;
  result: TestResult;
  identity: Identity;
  mapping: StatusMapping;
  attemptNo: number;
  artifactIds?: readonly string[];
  records?: readonly AttachmentRecord[];
}

export interface AttemptAppenderDeps {
  readonly ext: TestExtBuilder;
  readonly draft: DraftBuilder;
  readonly warn: (warning: ReporterWarning) => void;
  readonly stepsEnabled: boolean;
}

export class AttemptAppender {
  /** Raw statuses already reported by an `unknown_status` warning. */
  private readonly unknownStatusesWarned = new Set<string>();

  constructor(private readonly deps: AttemptAppenderDeps) {}

  append(writer: SpoolWriter, attempt: AttemptDraft): void {
    this.appendFinished(writer, attempt);
    this.appendUnknownStatusWarning(attempt);
    if (this.deps.stepsEnabled) this.appendSteps(writer, attempt);
    // One durable batch per test, so a killed run loses at most this test's envelopes.
    writer.flush();
  }

  private appendFinished(writer: SpoolWriter, attempt: AttemptDraft): void {
    const { test, result, identity, mapping, attemptNo } = attempt;
    // Only completed uploads, deduplicated, in attachment order: the id list must never
    // reference an artifact the server did not confirm.
    const artifactIds = [...new Set(attempt.artifactIds ?? [])];
    writer.append(
      this.deps.draft({
        type: 'test.finished',
        attempt_no: attemptNo,
        execution_key: identity.execution_key,
        status: mapping.raw,
        ext: this.deps.ext.testExt(test, result, identity, mapping, attempt.records),
        ...durationField(result.duration),
        ...(artifactIds.length === 0 ? {} : { artifact_ids: artifactIds }),
      }),
    );
  }

  /**
   * A status the adapter does not know is reported as `error`, never guessed. One warning
   * per distinct raw status, so a future status shared by many tests cannot spend the capped
   * warning budget that identity warnings need; the raw status is made printable because the
   * envelope `status` stays verbatim.
   */
  private appendUnknownStatusWarning(attempt: AttemptDraft): void {
    const { test, mapping } = attempt;
    if (mapping.unknown !== true || this.unknownStatusesWarned.has(mapping.raw)) return;
    this.unknownStatusesWarned.add(mapping.raw);
    const location = locationOf(test);
    this.deps.warn({
      code: 'unknown_status',
      message: `unknown Playwright status '${printable(mapping.raw)}' for ${location}; reported as error`,
      path: location,
    });
  }

  /** Step by step, so the working set stays at the flush threshold instead of the tree. */
  private appendSteps(writer: SpoolWriter, attempt: AttemptDraft): void {
    const { identity, attemptNo, result } = attempt;
    for (const step of walkSteps(result.steps)) {
      writer.append(
        this.deps.draft({
          type: 'test.step',
          attempt_no: attemptNo,
          execution_key: identity.execution_key,
          ext: this.deps.ext.stepExt(step, identity),
          ...durationField(step.duration),
        }),
      );
    }
  }
}

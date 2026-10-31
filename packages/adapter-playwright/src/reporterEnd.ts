// The end sequence: the identity summary, `run_end`, closing the spool, the identity gate and
// the buffered-failure report. It returns `{ status: 'failed' }` instead of throwing, because
// Playwright only routes a throwing reporter into its own error bookkeeping while the
// returned status is the documented failure channel. The override applies only to a `passed`
// run: an interrupted or timed-out run keeps its own status and exit code.
import type { FullResult } from '@playwright/test/reporter';

import { EXT_SCHEMA } from './envelope.js';
import { PLACEHOLDER_EXECUTION_KEY } from './identity.js';
import type { DraftBuilder } from './reporterDraft.js';
import { failureCode, toError } from './reporterFailure.js';
import type { IdentityBookkeeping } from './reporterIdentity.js';
import type { ReporterWarning, ReporterWarnings } from './reporterWarnings.js';
import type { SpoolWriter } from './spool.js';

export interface RunEndDeps {
  /** The writer of this run; undefined when the reporter emits nothing at all. */
  readonly writer: () => SpoolWriter | undefined;
  readonly draft: DraftBuilder;
  /** Appends one summary warning, bypassing the warning cap. */
  readonly emitWarning: (warning: ReporterWarning) => void;
  readonly identity: IdentityBookkeeping;
  readonly warnings: ReporterWarnings;
  readonly bufferFailure: (error: unknown) => void;
  readonly bufferedFailure: () => Error | null;
}

export class RunEndSequence {
  constructor(private readonly deps: RunEndDeps) {}

  finish(result: FullResult): { status: 'failed' } | undefined {
    const writer = this.deps.writer();
    if (writer !== undefined) {
      this.guarded(() => {
        this.emitSummaries();
        writer.append(
          this.deps.draft({
            type: 'run.log',
            attempt_no: 1,
            execution_key: PLACEHOLDER_EXECUTION_KEY,
            status: result.status,
            ext: { schema: EXT_SCHEMA, marker: 'run_end' },
          }),
        );
      });
    }
    const gateTripped = this.deps.identity.gateTripped;
    if (gateTripped) this.writeIdentityGateLine();
    const failure = this.deps.bufferedFailure() ?? this.closeWriter(writer);
    if (failure !== null) this.reportBufferedFailure(failure);
    if (result.status === 'passed' && (gateTripped || failure !== null)) {
      return { status: 'failed' };
    }
    return undefined;
  }

  /**
   * Both summary warnings bypass the cap, so the counts that explain a failure survive even
   * when the run already emitted the maximum number of warnings.
   */
  private emitSummaries(): void {
    const unidentified = this.deps.identity.unidentifiedCount;
    if (unidentified > 0) {
      this.deps.emitWarning({
        code: 'unmapped',
        message:
          `${unidentified} test(s) without a complete identity ` +
          `(${this.deps.identity.causes()})`,
        count: unidentified,
      });
    }
    const suppressed = this.deps.warnings.suppressed;
    if (suppressed > 0) {
      this.deps.emitWarning({
        code: 'warnings_truncated',
        message: `${suppressed} warning(s) suppressed by the reporter's warning caps`,
        count: suppressed,
      });
    }
  }

  /** One line, so a run that fails on identity says why in the CI log. */
  private writeIdentityGateLine(): void {
    process.stderr.write(
      `[evidgate] ${this.deps.identity.unidentifiedCount} test(s) without a complete ` +
        `identity (${this.deps.identity.causes()}); set EVIDGATE_ALLOW_UNMAPPED=1 to allow\n`,
    );
  }

  private closeWriter(writer: SpoolWriter | undefined): Error | null {
    if (writer === undefined) return null;
    try {
      writer.close();
      return null;
    } catch (error) {
      return toError(error);
    }
  }

  /**
   * Only an errno/reason code leaves the process: the message can carry a resolver-supplied
   * value, a presigned URL or the spool path, and none of those belong in a CI log.
   */
  private reportBufferedFailure(failure: Error): void {
    process.stderr.write(
      `[evidgate] reporter failure (${failureCode(failure)}); the run's evidence is incomplete\n`,
    );
  }

  private guarded(run: () => void): void {
    try {
      run();
    } catch (error) {
      this.deps.bufferFailure(error);
    }
  }
}

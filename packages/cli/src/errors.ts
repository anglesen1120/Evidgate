// 04.2.1-T2.1: the single error type every CLI failure path raises. The exit code is
// always 3 (`ExitCode.Error`) — the `code` string is the machine-readable subcode that
// reaches stderr as `evidgate: error <code>: <message>` (04.2.1 §5, AC4/AC5).
import { ExitCode } from './exitCodes.js';

export interface CliErrorOptions {
  /** Machine-readable subcode, e.g. `missing_config`, `dev_token_refused`, `usage`. */
  readonly code: string;
  /** Defaults to `ExitCode.Error` (3); auth/config subcodes never change it. */
  readonly exitCode?: ExitCode;
  /** Optional remediation line, printed as `evidgate: hint: <hint>`. */
  readonly hint?: string;
  /** Optional server request id, printed as `evidgate: request_id: <id>`. */
  readonly requestId?: string;
}

export class CliError extends Error {
  readonly code: string;
  readonly exitCode: ExitCode;
  readonly hint?: string;
  readonly requestId?: string;

  constructor(message: string, options: CliErrorOptions) {
    super(message);
    this.name = 'CliError';
    this.code = options.code;
    this.exitCode = options.exitCode ?? ExitCode.Error;
    if (options.hint !== undefined) {
      this.hint = options.hint;
    }
    if (options.requestId !== undefined) {
      this.requestId = options.requestId;
    }
  }
}

/** Renders the stderr block for a `CliError` (no trailing newline). */
export function formatCliError(error: CliError): string {
  const lines = [`evidgate: error ${error.code}: ${error.message}`];
  if (error.hint !== undefined) {
    lines.push(`evidgate: hint: ${error.hint}`);
  }
  if (error.requestId !== undefined) {
    lines.push(`evidgate: request_id: ${error.requestId}`);
  }
  return lines.join('\n');
}

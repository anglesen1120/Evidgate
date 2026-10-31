// The single error type every CLI failure path raises. The exit code is always 3
// (`ExitCode.Error`); `code` is the machine-readable subcode printed as
// `evidgate: error <code>: <message>`. The constructor takes no exit-code override, so a
// subcode can never change the process exit code (contracts/cli.md §2).
export interface CliErrorOptions {
  /** Machine-readable subcode, e.g. `missing_config`, `dev_token_refused`, `usage`. */
  readonly code: string;
  /** Optional remediation line, printed as `evidgate: hint: <hint>`. */
  readonly hint?: string;
  /** Optional server request id, printed as `evidgate: request_id: <id>`. */
  readonly requestId?: string;
  /**
   * Optional per-item server detail lines, printed one per entry as
   * `evidgate: detail: <detail>` after the hint/request_id lines (contracts/cli.md §5).
   */
  readonly details?: readonly string[];
}

export class CliError extends Error {
  readonly code: string;
  readonly hint?: string;
  readonly requestId?: string;
  readonly details?: readonly string[];

  constructor(message: string, options: CliErrorOptions) {
    super(message);
    this.name = 'CliError';
    this.code = options.code;
    if (options.hint !== undefined) {
      this.hint = options.hint;
    }
    if (options.requestId !== undefined) {
      this.requestId = options.requestId;
    }
    if (options.details !== undefined) {
      this.details = options.details;
    }
  }
}

const controlOrFormat = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu;

/**
 * Escapes Unicode control (`Cc`), format (`Cf`) and line/paragraph separator (`Zl`/`Zp`)
 * characters as `\uXXXX`, so a value interpolated into a message cannot forge a log line
 * or a GitHub `::` workflow command (contracts/cli.md §5, SEC4). A newline therefore
 * never passes through, and neither do U+2028/U+2029, which some log viewers and
 * JSON-lines consumers render as line breaks.
 */
export function escapeControlCharacters(value: string): string {
  return value.replace(controlOrFormat, (character) => {
    const codePoint = character.codePointAt(0) ?? 0;
    return codePoint <= 0xffff
      ? `\\u${codePoint.toString(16).padStart(4, '0')}`
      : `\\u{${codePoint.toString(16)}}`;
  });
}

/**
 * Ensures every line of `text` starts with `evidgate: `, so no stderr line can start
 * with `::` (a GitHub workflow command). Idempotent — a line that already carries the
 * prefix is left alone — and a single trailing newline is preserved
 * (contracts/cli.md §5, SEC4).
 */
export function prefixStderrLines(text: string): string {
  if (text.length === 0) {
    return text;
  }
  const endsWithNewline = text.endsWith('\n');
  const body = endsWithNewline ? text.slice(0, -1) : text;
  const prefixed = body
    .split('\n')
    .map((line) => (line.startsWith('evidgate: ') ? line : `evidgate: ${line}`))
    .join('\n');
  return endsWithNewline ? `${prefixed}\n` : prefixed;
}

/** Renders the stderr block for a `CliError` (no trailing newline). */
export function formatCliError(error: CliError): string {
  const lines = [`evidgate: error ${error.code}: ${escapeControlCharacters(error.message)}`];
  if (error.hint !== undefined) {
    lines.push(`evidgate: hint: ${escapeControlCharacters(error.hint)}`);
  }
  if (error.requestId !== undefined) {
    lines.push(`evidgate: request_id: ${escapeControlCharacters(error.requestId)}`);
  }
  for (const detail of error.details ?? []) {
    lines.push(`evidgate: detail: ${escapeControlCharacters(detail)}`);
  }
  return lines.join('\n');
}

/**
 * Renders the `internal_error` stderr line for an unexpected (non-`CliError`) failure
 * (no trailing newline). Shared by `main`'s catch-all and `bin.ts`'s process-level
 * backstop so the two can never drift (contracts/cli.md §5).
 */
export function formatInternalError(error: unknown): string {
  const message = error instanceof Error ? error.message : 'unexpected non-Error failure';
  return `evidgate: error internal_error: ${escapeControlCharacters(message)}`;
}

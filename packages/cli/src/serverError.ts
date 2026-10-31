// Maps the server's error envelope to `CliError` so the exchange and manifest-upload paths
// cannot drift into two spellings of one rule (contracts/cli.md §5): `error.code` is echoed
// only when it is a safe identifier, and every server-supplied text field is redacted,
// capped and control-char escaped. A transport failure keeps a fixed message, so a URL or
// credential it might embed is never echoed.
import { isErrorEnvelope, type ErrorEnvelope } from '@evidgate/sdk';

import { CliError, escapeControlCharacters } from './errors.js';
import { redact } from './redact.js';

/** The code/message used when the body is not a usable 00 §9.2 envelope. */
export interface ServerErrorFallback {
  readonly code: string;
  readonly message: string;
}

/** A server `error.code` is echoed only when it is a safe identifier (contracts/cli.md §5). */
const serverCodePattern = /^[a-z][a-z0-9_]{0,63}$/;
const maxMessageLength = 200;
/** At most this many `detail:` lines; the rest collapse into one `(<k> more)` line. */
const maxDetailLines = 50;
const maxDetailFieldLength = 200;

/**
 * Redacts, caps to `max` Unicode code points and control-char escapes one server-supplied
 * text field (contracts/cli.md §5, SEC4). Redaction runs first, so a registered secret
 * straddling the cut can never leave a clear-text prefix; the cap counts code points, so
 * a surrogate pair is never split; escaping runs last, so a `\uXXXX` escape is never cut.
 */
function sanitizeServerText(raw: string, max: number): string {
  const capped = Array.from(redact(raw)).slice(0, max).join('');
  return escapeControlCharacters(capped);
}

/**
 * Maps a non-2xx reply to a `CliError`: the server's `error.code` when it is a safe
 * identifier (else `fallback.code`), the server's message sanitized and capped at 200
 * code points, `request_id` from the body else the `X-Request-Id` header, and one
 * `detail:` line per `error.details[]` entry. Nothing else of the body is echoed
 * (contracts/cli.md §5).
 */
export function serverErrorFromResponse(
  error: unknown,
  response: Response,
  fallback: ServerErrorFallback,
): CliError {
  const headerRequestId = response.headers.get('x-request-id') ?? undefined;
  if (!isErrorEnvelope(error)) {
    return new CliError(fallback.message, {
      code: fallback.code,
      ...(headerRequestId !== undefined
        ? { requestId: sanitizeServerText(headerRequestId, maxMessageLength) }
        : {}),
    });
  }
  const body = error.error;
  const code = serverCodePattern.test(body.code) ? body.code : fallback.code;
  const rawRequestId = body.request_id.length > 0 ? body.request_id : headerRequestId;
  const requestId =
    rawRequestId !== undefined ? sanitizeServerText(rawRequestId, maxMessageLength) : undefined;
  const message = sanitizeServerText(body.message, maxMessageLength);
  const details = detailLines(body.details);
  return new CliError(message, {
    code,
    ...(requestId !== undefined ? { requestId } : {}),
    ...(details.length > 0 ? { details } : {}),
  });
}

/**
 * One `index=<n> path=<p> reason=<r>` line per `error.details[]` entry
 * (contracts/cli.md §5): `index=` is omitted when the entry has none, a field that is
 * not a string or a number is skipped, and every text field is redacted, capped at 200
 * code points and control-char escaped. At most `maxDetailLines` lines, then one
 * `(<k> more)` line.
 */
function detailLines(details: ErrorEnvelope['error']['details']): string[] {
  if (details === undefined) {
    return [];
  }
  const lines = details.slice(0, maxDetailLines).map((detail) => {
    const fields: string[] = [];
    if (typeof detail.index === 'number') {
      fields.push(`index=${detail.index}`);
    }
    if (typeof detail.path === 'string') {
      fields.push(`path=${sanitizeServerText(detail.path, maxDetailFieldLength)}`);
    }
    if (typeof detail.reason === 'string') {
      fields.push(`reason=${sanitizeServerText(detail.reason, maxDetailFieldLength)}`);
    }
    return fields.join(' ');
  });
  const remaining = details.length - maxDetailLines;
  if (remaining > 0) {
    lines.push(`(${remaining} more)`);
  }
  return lines;
}

/** A transport failure is reported with a fixed message; the cause may echo a URL or token. */
export function transportError(cause: unknown): CliError {
  const timedOut =
    cause instanceof Error && (cause.name === 'AbortError' || cause.name === 'TimeoutError');
  return new CliError(timedOut ? 'the request timed out' : 'the request failed', {
    code: 'transport',
  });
}

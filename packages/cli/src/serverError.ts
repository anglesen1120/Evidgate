// 04.2.2-T1.1: the 00 §9.2 error envelope → `CliError` mapping shared by the
// project-scoped commands (contracts/cli.md §5). `auth/exchange.ts` uses it for the
// token exchange and `manifests upload` for the manifest upload, so the two cannot
// drift into two spellings of one rule: the server's `error.code` is echoed only when
// it is a safe identifier, the message is escaped and capped, `request_id` falls back
// to the `X-Request-Id` header, and `error.details[]` becomes one `detail:` line each.
// A transport failure is reported with a fixed message, so the cause — which may embed
// a URL or a credential — is never echoed.
import { isErrorEnvelope, type ErrorEnvelope } from '@evidgate/sdk';

import { CliError, escapeControlCharacters } from './errors.js';

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
 * Maps a non-2xx reply to a `CliError`: the server's `error.code` when it is a safe
 * identifier (else `fallback.code`), the server's message escaped and capped at 200
 * chars, `request_id` from the body else the `X-Request-Id` header, and one `detail:`
 * line per `error.details[]` entry. Nothing else of the body is echoed
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
      ...(headerRequestId !== undefined ? { requestId: headerRequestId } : {}),
    });
  }
  const body = error.error;
  const code = serverCodePattern.test(body.code) ? body.code : fallback.code;
  const requestId = body.request_id.length > 0 ? body.request_id : headerRequestId;
  const message = escapeControlCharacters(body.message);
  const details = detailLines(body.details);
  return new CliError(
    message.length > maxMessageLength ? message.slice(0, maxMessageLength) : message,
    {
      code,
      ...(requestId !== undefined ? { requestId } : {}),
      ...(details.length > 0 ? { details } : {}),
    },
  );
}

/**
 * One `index=<n> path=<p> reason=<r>` line per `error.details[]` entry
 * (contracts/cli.md §5): `index=` is omitted when the entry has none, a field that is
 * not a string or a number is skipped, and every text field is control-char escaped
 * and capped at 200 chars. At most `maxDetailLines` lines, then one `(<k> more)` line.
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
      const path = escapeControlCharacters(detail.path);
      fields.push(
        `path=${path.length > maxDetailFieldLength ? path.slice(0, maxDetailFieldLength) : path}`,
      );
    }
    if (typeof detail.reason === 'string') {
      const reason = escapeControlCharacters(detail.reason);
      fields.push(
        `reason=${reason.length > maxDetailFieldLength ? reason.slice(0, maxDetailFieldLength) : reason}`,
      );
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

// 04.2.4-T1.2: the upload retry layer (04.2.4 §5 retry rule, §8; contracts/cli.md §12).
//
// One policy for all three calls of the upload (upload-url, the presigned PUT, complete):
// only 408/429/5xx and transport failures are retried, with the SAME full-jitter backoff,
// `Retry-After` floor and per-request timeout the 04.2.3 sender uses — `backoffDelay`,
// `retryAfter`, `attemptSignal` and `isRetryable` are imported from `sender.ts`, so there
// is one convention, not two. A deterministic 4xx (401/403/404/409/412/413/422) is never
// retried: it is returned to the caller as-is and surfaces verbatim.
//
// The retry loop is bounded by one deadline for the whole upload
// (`EVIDGATE_UPLOAD_DEADLINE`, mirroring `EVIDGATE_SEND_DEADLINE`'s grammar): a wait that
// would reach the deadline fails `deadline_exceeded` instead of sleeping, and the message
// names the last error's code/status/request id — never the URL, the file content or the
// bearer. An expired presigned URL is NOT a retry of the PUT: `upload.ts` re-mints, because
// the same signed URL can never work again.
import { CliError } from './errors.js';
import { attemptSignal, backoffDelay } from './sender.js';

/** `EVIDGATE_UPLOAD_DEADLINE` default: 30 minutes, the same budget `events send` gets. */
export const defaultUploadDeadlineMs = 1_800_000;
/** Ceiling: 24 hours, the `EVIDGATE_SEND_DEADLINE` ceiling (contracts/cli.md §3). */
export const maxUploadDeadlineMs = 86_400_000;
/**
 * A backstop on the number of attempts, so a pathological responder that answers 503
 * instantly cannot spin through the deadline in a tight loop. Real backoff reaches the
 * 30-minute deadline far earlier.
 */
export const maxUploadAttempts = 20;

/** The seams a deadline-bounded upload needs; a spec injects all of them. */
export interface UploadRetryDeps {
  /** Virtual sleep; a spec advances its clock here instead of waiting. */
  readonly sleep: (milliseconds: number, signal?: AbortSignal) => Promise<void>;
  /** Clock in milliseconds; it drives the one deadline. */
  readonly now: () => number;
  /** Uniform sample in `[0, 1]` for full jitter. */
  readonly random: () => number;
}

/**
 * One attempt's result. `retryAfterMs` is the server's `Retry-After` when it sent one; the
 * wait is the longer of that and the jittered backoff, so `Retry-After: 0` cannot spin.
 */
export type Attempt<T> =
  | { readonly ok: true; readonly value: T }
  | {
      readonly ok: false;
      readonly error: unknown;
      readonly retryable: boolean;
      readonly retryAfterMs?: number;
    };

export interface RetryOptions {
  /** Absolute deadline (`now() + EVIDGATE_UPLOAD_DEADLINE`). */
  readonly deadlineAt: number;
  readonly deps: UploadRetryDeps;
  /** One line naming the last error for `deadline_exceeded`; must not carry a secret. */
  readonly describe: (error: unknown) => string;
}

/** The bare-integer-seconds or `<n>s` / `<n>m` / `<n>h` grammar of contracts/cli.md §3. */
const deadlinePattern = /^([0-9]+)([smh]?)$/;

/**
 * `EVIDGATE_UPLOAD_DEADLINE`: one deadline for a whole artifact upload. Same grammar and
 * bounds as `EVIDGATE_SEND_DEADLINE` (§3): a bare integer is seconds, an `s`/`m`/`h`
 * suffix scales it, default 1800000 ms, ceiling 86400000 ms. Zero, a fraction, a unit
 * suffix this grammar does not know and a longer value are `invalid_config` naming the
 * variable; the raw value is never echoed (it could forge a log line).
 */
export function resolveUploadDeadlineMs(env: Readonly<Record<string, string | undefined>>): number {
  const name = 'EVIDGATE_UPLOAD_DEADLINE';
  const raw = env[name]?.trim();
  if (raw === undefined || raw === '') {
    return defaultUploadDeadlineMs;
  }
  const match = deadlinePattern.exec(raw);
  if (match === null) {
    throw invalidDeadline(name);
  }
  const amount = Number(match[1]);
  const scale = match[2] === 'h' ? 3_600_000 : match[2] === 'm' ? 60_000 : 1_000;
  const milliseconds = amount * scale;
  if (
    !Number.isSafeInteger(milliseconds) ||
    milliseconds <= 0 ||
    milliseconds > maxUploadDeadlineMs
  ) {
    throw invalidDeadline(name);
  }
  return milliseconds;
}

function invalidDeadline(name: string): CliError {
  return new CliError(`invalid configuration: ${name}: must be a positive duration`, {
    code: 'invalid_config',
    hint: 'use seconds, or an s/m/h suffix, at most 24 hours (for example EVIDGATE_UPLOAD_DEADLINE=30m)',
  });
}

/**
 * Runs `attempt` until it succeeds, fails permanently, or the deadline forbids another
 * wait. A request whose own timeout aborts is a transport failure the caller classifies as
 * retryable — the same rule `sender.ts` applies.
 */
export async function retryAttempts<T>(
  attempt: (attemptNo: number, signal: AbortSignal) => Attempt<T> | Promise<Attempt<T>>,
  options: RetryOptions,
): Promise<T> {
  let lastError: unknown;
  for (let attemptNo = 1; attemptNo <= maxUploadAttempts; attemptNo += 1) {
    const signal = attemptSignal(options.deadlineAt, options.deps.now());
    const outcome = await attempt(attemptNo, signal);
    if (outcome.ok) {
      return outcome.value;
    }
    lastError = outcome.error;
    if (!outcome.retryable) {
      throw outcome.error;
    }
    if (attemptNo === maxUploadAttempts) {
      break;
    }
    const delay = Math.max(
      backoffDelay(attemptNo, options.deps.random()),
      outcome.retryAfterMs ?? 0,
    );
    if (options.deps.now() + delay > options.deadlineAt) {
      throw deadlineExceeded(options.describe(lastError));
    }
    await options.deps.sleep(delay, signal);
  }
  // Attempts exhausted but the clock still allows: the caller gets the last real error,
  // not a synthetic one, so the operator sees the server's own code.
  throw lastError;
}

/** The one failure a spent upload deadline raises: no URL, no file content, no bearer. */
export function deadlineExceeded(last: string): CliError {
  return new CliError(
    `the artifact upload did not finish before its deadline; last error: ${last}`,
    {
      code: 'deadline_exceeded',
      hint: 'raise EVIDGATE_UPLOAD_DEADLINE or retry; a retried upload never creates a second artifact row',
    },
  );
}

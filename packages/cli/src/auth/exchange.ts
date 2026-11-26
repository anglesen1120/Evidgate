import { exchangeCiToken } from '@evidgate/sdk';

import { callApi } from '../callApi.js';
import { createApiClient } from '../client.js';
import { envNames, type ResolvedConfig } from '../config.js';
import { CliError, type CliErrorOptions } from '../errors.js';
import { registerSecret } from '../redact.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';

import { deleteCachedToken } from './tokenCache.js';

export interface ExchangedToken {
  /** Registered with the redactor before any validation or return, so no message can leak it. */
  readonly token: string;
  /** ISO-8601 expiry from the server. */
  readonly expiresAt: string;
  readonly projectId: string;
  readonly source: 'ingest';
}

/** Injected seams so a spec can stub transport, the clock and the retry back-off. */
export interface ExchangeDeps {
  readonly fetch?: typeof fetch;
  readonly sleep?: (ms: number) => Promise<void>;
  /** Uniform sample in `[0, 1)` for the jittered retry backoff. */
  readonly random?: () => number;
  /** Clock in milliseconds; it drives the retry budget and an HTTP-date `Retry-After`. */
  readonly now?: () => number;
  /**
   * Absolute instant, on the `now` scale, that the exchange may not pass. Absent leaves it the
   * exchange's own budget; a caller polling to a `--wait` deadline lowers it to what is left.
   */
  readonly deadlineAt?: number;
}

/** Thrown for HTTP 401/403 from `/v1/ci/token`; the caller purges the cache entry. */
export class ExchangeAuthError extends CliError {
  readonly status: number;

  constructor(message: string, options: CliErrorOptions & { readonly status: number }) {
    super(message, options);
    this.name = 'ExchangeAuthError';
    this.status = options.status;
  }
}

/** The exchange's own budget: two request timeouts plus the 60 s `Retry-After` cap. */
const exchangeBudget = 120_000;
/** The wait contracts/cli.md §5 documents when a 429 names no usable `Retry-After`. */
const defaultRetryAfterMs = 1_000;
/** Strict RFC 3339 `date-time` (openapi); a value that is not one is refused un-echoed (S1). */
const rfc3339Pattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$/;
/** The `project_id` is a UUID (openapi `format: uuid`); a non-UUID is refused un-echoed (S1). */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Exchanges the project ingest token for a short-lived producer token. No other credential is
 * tried. Only pre-processing refusals may be retried: a lost answer may already have minted one.
 */
export async function exchangeToken(
  config: ResolvedConfig,
  deps: ExchangeDeps = {},
): Promise<ExchangedToken> {
  const ingestToken = config.ingestToken;
  if (ingestToken === undefined) {
    throw new CliError(`missing configuration: ${envNames.ingestToken}`, {
      code: 'missing_config',
    });
  }
  assertDeadlineLeft(deps);
  const client = createApiClient(config, undefined, deps.fetch ?? fetch);
  const body = { ingest_token: ingestToken };

  const result = await callApi((signal) => exchangeCiToken({ client, body, signal }), {
    policy: 'pre-processing',
    maxAttempts: 2,
    budgetMs: exchangeBudgetMsFor(deps),
    fallbackRetryAfterMs: defaultRetryAfterMs,
    deps,
  });
  const response = result.response;
  if (response === undefined) {
    // A network error, a timeout or a refused redirect: the SDK returns no response.
    throw transportError(result.error);
  }
  if (response.ok) {
    return readSuccess(result.data);
  }
  const envelope = readEnvelope(result.error, response);
  if (response.status === 401 || response.status === 403) {
    throw new ExchangeAuthError(envelope.message, {
      ...envelope.options,
      status: response.status,
    });
  }
  throw new CliError(envelope.message, envelope.options);
}

/**
 * The exchange budget, lowered to what a caller's deadline leaves. `callApi` applies its own
 * 1 ms floor, so a caller that checks first still gets one request whose abort ends it.
 */
function exchangeBudgetMsFor(deps: ExchangeDeps): number {
  if (deps.deadlineAt === undefined) {
    return exchangeBudget;
  }
  const now = deps.now === undefined ? Date.now() : deps.now();
  return Math.min(exchangeBudget, deps.deadlineAt - now);
}

/**
 * Refuses to open a request for a deadline that has already passed. `attemptSignal` falls back
 * to the plain per-request timeout once nothing is left, so a request started here could keep a
 * socket open for 30 s after the caller has already decided its exit code.
 */
function assertDeadlineLeft(deps: ExchangeDeps): void {
  const now = deps.now === undefined ? Date.now() : deps.now();
  if (deps.deadlineAt !== undefined && deps.deadlineAt - now <= 0) {
    throw new CliError('the token exchange was not started: its deadline had already passed', {
      code: 'request_failed',
    });
  }
}

/**
 * Exchanges the credential and, when the server answers 401/403, purges the cached entry
 * first so the next run re-authenticates cleanly. A purge failure is swallowed: the server's
 * error is the one that must surface.
 */
export async function exchangeAndPurge(
  config: ResolvedConfig,
  deps: ExchangeDeps = {},
): Promise<ExchangedToken> {
  try {
    return await exchangeToken(config, deps);
  } catch (error) {
    if (error instanceof ExchangeAuthError) {
      await deleteCachedToken(config).catch(() => undefined);
    }
    throw error;
  }
}

/** Validates the 200 body; the token is registered for redaction before validation. */
function readSuccess(data: unknown): ExchangedToken {
  const body: Record<string, unknown> =
    typeof data === 'object' && data !== null ? (data as Record<string, unknown>) : {};
  const token = body['token'];
  if (typeof token === 'string') {
    registerSecret(token);
  }
  const expiresAt = body['expires_at'];
  const projectId = body['project_id'];
  if (
    typeof token !== 'string' ||
    token.length === 0 ||
    /\s/.test(token) ||
    typeof expiresAt !== 'string' ||
    !rfc3339Pattern.test(expiresAt) ||
    !Number.isFinite(Date.parse(expiresAt)) ||
    typeof projectId !== 'string' ||
    !uuidPattern.test(projectId)
  ) {
    throw new CliError('the token exchange response was not valid', { code: 'exchange_failed' });
  }
  return { token, expiresAt, projectId, source: 'ingest' };
}

interface EnvelopeInfo {
  readonly message: string;
  readonly options: CliErrorOptions;
}

/** Preserve the exchange error code and request id without echoing unrecognized bodies. */
function readEnvelope(error: unknown, response: Response): EnvelopeInfo {
  const mapped = serverErrorFromResponse(error, response, {
    code: 'exchange_failed',
    message: 'the token exchange failed',
  });
  return {
    message: mapped.message,
    options: {
      code: mapped.code,
      ...(mapped.requestId !== undefined ? { requestId: mapped.requestId } : {}),
    },
  };
}

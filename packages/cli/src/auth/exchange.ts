// The OIDC/ingest-token exchange at `POST /v1/ci/token`: resolves the token source, obtains
// the credential (env OIDC JWT, GitHub Actions ID token, or ingest token), calls the SDK
// operation through the redirect-refusing client, and maps a server refusal to a `CliError`
// carrying the server's `error.code` when it is a safe identifier (else `exchange_failed`);
// client-side failures carry the CLI's own codes (contracts/cli.md §5). The exchanged token is
// registered with the redactor before it is validated or returned, so it can never reach a
// writer.
import { exchangeCiToken, type CiTokenRequest } from '@evidgate/sdk';

import { callApi } from '../callApi.js';
import { createApiClient } from '../client.js';
import { envNames, isLoopbackApiUrl, resolveTokenSource, type ResolvedConfig } from '../config.js';
import { CliError, type CliErrorOptions } from '../errors.js';
import { registerSecret } from '../redact.js';
import { attemptSignal } from '../sender.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';

import { deleteCachedToken } from './tokenCache.js';

/** The credential the exchange presents; a dev token reaches it as `ingest` (contracts/cli.md §4). */
export type ExchangeSource = 'oidc' | 'actions-id-token' | 'ingest';

export interface ExchangedToken {
  /** Registered with the redactor before any validation or return, so no message can leak it. */
  readonly token: string;
  /** ISO-8601 expiry from the server. */
  readonly expiresAt: string;
  readonly projectId: string;
  readonly source: ExchangeSource;
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
/** The audience the server's `CiAuthOptions.GithubOidcAudience` expects. */
const githubOidcAudience = 'evidgate';
/** Strict RFC 3339 `date-time` (openapi); a value that is not one is refused un-echoed (S1). */
const rfc3339Pattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$/;
/** The `project_id` is a UUID (openapi `format: uuid`); a non-UUID is refused un-echoed (S1). */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The `missing_config` entry for "no exchangeable source is set" (`getToken` maps a dev token to `ingest` first). */
const exchangeSourceNames =
  `an exchange token source: ${envNames.oidcToken}, ` +
  `${envNames.actionsIdTokenRequestUrl} + ${envNames.actionsIdTokenRequestToken} or ` +
  `${envNames.ingestToken}`;

/** Remediation lines for the two binding states; the server's message alone is not actionable. */
const bindingHints: Readonly<Record<string, string>> = {
  binding_pending:
    'the repository has not presented the binding challenge yet — set the repository ' +
    'variable EVIDGATE_BINDING_CHALLENGE to the challenge shown when the binding was created',
  binding_conflict:
    'this repository is already bound to another organization; its admin must disable that ' +
    'binding first',
};

/**
 * Exchanges the configured credential for a short-lived `ci_producer` token. openapi
 * `/ci/token` is "Idempotency-Key exempt: `jti` single-use is the arbiter", so a replay
 * is not free: only the two answers the server produces BEFORE it mints a token are
 * retried — 429 `rate_limited` and 503 `unavailable` (no signing key / issuer keys
 * unreachable). A transport failure, a 500/502/504 or any other status is final, because
 * the token may already exist and the single-use `jti` would refuse the replay. That makes
 * the retry safe in the other direction too: the server's `jti` arbiter caps an exchange at
 * one token, so no retry path here can mint a second one — the worst case is a refused
 * replay the command reports as `token_replayed`.
 * 401/403 raise `ExchangeAuthError`; every other failure raises a `CliError`
 * (contracts/cli.md §5).
 */
export async function exchangeToken(
  config: ResolvedConfig,
  deps: ExchangeDeps = {},
): Promise<ExchangedToken> {
  const source = resolveTokenSource(config);
  if (source === undefined || source === 'dev') {
    throw new CliError(`missing configuration: ${exchangeSourceNames}`, { code: 'missing_config' });
  }
  assertDeadlineLeft(deps);
  const fetchImpl = deps.fetch ?? fetch;
  const credential = await resolveCredential(config, source, fetchImpl, deps);
  const client = createApiClient(config, credential.bearer, fetchImpl);
  const body = buildBody(config, source, credential);

  const result = await callApi((signal) => exchangeCiToken({ client, body, signal }), {
    // One retry, the pre-tenant exchange's own budget: a credential is single-use, so a
    // second replay buys nothing, and contracts/cli.md §5 documents the 1 s floor for a
    // 429 that names no usable `Retry-After`. The budget holds two request timeouts plus
    // the 60 s `Retry-After` cap, so a documented wait never blocks the single retry, and a
    // caller's deadline lowers it so the exchange cannot outlive the wait that asked for it.
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
    return readSuccess(result.data, source);
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

/** The bearer (OIDC/ID token) or the ingest token the exchange presents. */
interface Credential {
  readonly bearer: string | undefined;
  readonly ingestToken: string | undefined;
}

async function resolveCredential(
  config: ResolvedConfig,
  source: ExchangeSource,
  fetchImpl: typeof fetch,
  deps: ExchangeDeps,
): Promise<Credential> {
  if (source === 'ingest') {
    const ingestToken = config.ingestToken;
    if (ingestToken === undefined) {
      throw new CliError(`missing configuration: ${envNames.ingestToken}`, {
        code: 'missing_config',
      });
    }
    return { bearer: undefined, ingestToken };
  }
  if (source === 'oidc') {
    const jwt = config.oidcToken;
    if (jwt === undefined) {
      throw new CliError(`missing configuration: ${envNames.oidcToken}`, {
        code: 'missing_config',
      });
    }
    return { bearer: jwt, ingestToken: undefined };
  }
  return { bearer: await fetchActionsIdToken(config, fetchImpl, deps), ingestToken: undefined };
}

/** `{ binding_challenge }` only when configured; the ingest body also carries the token. */
function buildBody(
  config: ResolvedConfig,
  source: ExchangeSource,
  credential: Credential,
): CiTokenRequest {
  const challenge = config.bindingChallenge;
  if (source === 'ingest') {
    return {
      ingest_token: credential.ingestToken ?? null,
      ...(challenge !== undefined ? { binding_challenge: challenge } : {}),
    };
  }
  return challenge !== undefined ? { binding_challenge: challenge } : {};
}

/**
 * Fetches a GitHub Actions ID token from `ACTIONS_ID_TOKEN_REQUEST_URL` with the `evidgate`
 * audience. The URL must be `https:` (or `http:` on a loopback host); a non-2xx reply or a
 * body without a `value` is `oidc_request_failed`, a transport failure is `transport`.
 */
async function fetchActionsIdToken(
  config: ResolvedConfig,
  fetchImpl: typeof fetch,
  deps: ExchangeDeps,
): Promise<string> {
  const requestUrl = config.actionsIdTokenRequestUrl;
  const requestToken = config.actionsIdTokenRequestToken;
  if (requestUrl === undefined || requestToken === undefined) {
    throw new CliError(
      `missing configuration: ${envNames.actionsIdTokenRequestUrl} + ` +
        `${envNames.actionsIdTokenRequestToken}`,
      { code: 'missing_config' },
    );
  }
  const url = parseActionsIdTokenUrl(requestUrl);
  url.searchParams.set('audience', githubOidcAudience);
  const now = deps.now === undefined ? Date.now() : deps.now();

  let response: Response;
  try {
    response = await fetchImpl(url, {
      headers: { Authorization: `bearer ${requestToken}` },
      redirect: 'error',
      // `attemptSignal` adds the caller's deadline when it has one (an infinite instant leaves
      // the 30 s per-request timeout alone), so a `--wait` refresh cannot outlive the wait.
      signal: attemptSignal(deps.deadlineAt ?? Number.POSITIVE_INFINITY, now),
    });
  } catch (error) {
    throw transportError(error);
  }
  if (!response.ok) {
    // Release the body before throwing so the connection can be reused (P2).
    await response.body?.cancel().catch(() => undefined);
    throw new CliError('the GitHub Actions ID token request failed', {
      code: 'oidc_request_failed',
    });
  }
  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    throw new CliError('the GitHub Actions ID token response was not valid JSON', {
      code: 'oidc_request_failed',
    });
  }
  const value =
    typeof payload === 'object' && payload !== null
      ? (payload as Record<string, unknown>)['value']
      : undefined;
  if (typeof value !== 'string' || value.length === 0) {
    throw new CliError('the GitHub Actions ID token response had no value', {
      code: 'oidc_request_failed',
    });
  }
  registerSecret(value);
  return value;
}

/** Names the variable without echoing its value (it may carry a signed request token). */
function parseActionsIdTokenUrl(raw: string): URL {
  let url: URL | undefined;
  try {
    url = new URL(raw);
  } catch {
    url = undefined;
  }
  const loopbackHttp = url !== undefined && url.protocol === 'http:' && isLoopbackApiUrl(raw);
  if (url === undefined || (url.protocol !== 'https:' && !loopbackHttp)) {
    throw new CliError(
      `invalid configuration: ${envNames.actionsIdTokenRequestUrl}: must be an https: URL ` +
        `(or http: on a loopback host)`,
      { code: 'invalid_config' },
    );
  }
  if (url.username !== '' || url.password !== '' || url.hash !== '') {
    throw new CliError(
      `invalid configuration: ${envNames.actionsIdTokenRequestUrl}: must not carry ` +
        `credentials or a fragment`,
      { code: 'invalid_config' },
    );
  }
  return url;
}

/** Validates the 200 body; the token is registered for redaction before validation. */
function readSuccess(data: unknown, source: ExchangeSource): ExchangedToken {
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
  return { token, expiresAt, projectId, source };
}

interface EnvelopeInfo {
  readonly message: string;
  readonly options: CliErrorOptions;
}

/**
 * Reads the 00 §9.2 envelope through the shared mapper; an unusable body becomes
 * `exchange_failed` (never echoed). The two binding codes keep their remediation hint,
 * and `error.details[]` is dropped: `/v1/ci/token` has no 422, so the exchange never
 * emitted a `detail:` line and must not start now (contracts/cli.md §5/§7).
 */
function readEnvelope(error: unknown, response: Response): EnvelopeInfo {
  const mapped = serverErrorFromResponse(error, response, {
    code: 'exchange_failed',
    message: 'the token exchange failed',
  });
  const hint = bindingHints[mapped.code];
  return {
    message: mapped.message,
    options: {
      code: mapped.code,
      ...(hint !== undefined ? { hint } : {}),
      ...(mapped.requestId !== undefined ? { requestId: mapped.requestId } : {}),
    },
  };
}

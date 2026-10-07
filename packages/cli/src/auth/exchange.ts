// 04.2.1-T1.1: the OIDC/ingest-token exchange at `POST /v1/ci/token` (04.2.1 §5
// `auth/refresh.ts`, AC1/AC2/AC4/AC7). It resolves the token source, obtains the
// credential (the env OIDC JWT, a GitHub Actions ID token, or the ingest token), calls
// the SDK operation with the redirect-refusing client, and maps every failure to a
// `CliError` whose `code` echoes the server's `error.code` verbatim (contracts/cli.md
// §5). The exchanged token is registered with the redactor before it is validated or
// returned, so it can never reach a writer (T78).
import { setTimeout as delay } from 'node:timers/promises';

import { exchangeCiToken, isErrorEnvelope, type CiTokenRequest } from '@evidgate/sdk';

import { createApiClient } from '../client.js';
import { envNames, isLoopbackApiUrl, resolveTokenSource, type ResolvedConfig } from '../config.js';
import { CliError, escapeControlCharacters, type CliErrorOptions } from '../errors.js';
import { registerSecret } from '../redact.js';

import { deleteCachedToken } from './tokenCache.js';

/** The credential the exchange presents; `dev` is never exchanged (contracts/cli.md §4). */
export type ExchangeSource = 'oidc' | 'actions-id-token' | 'ingest';

export interface ExchangedToken {
  /** Registered with the redactor before any validation or return (T78). */
  readonly token: string;
  /** ISO-8601 expiry from the server. */
  readonly expiresAt: string;
  readonly projectId: string;
  readonly source: ExchangeSource;
}

/** Injected seams so a spec can stub transport and the 429 back-off. */
export interface ExchangeDeps {
  readonly fetch?: typeof fetch;
  readonly sleep?: (ms: number) => Promise<void>;
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

const requestTimeoutMs = 30_000;
const maxRetryAfterMs = 60_000;
const defaultRetryAfterMs = 1_000;
const maxMessageLength = 200;
/** The audience the server's `CiAuthOptions.GithubOidcAudience` expects. */
const githubOidcAudience = 'evidgate';
/** A server `error.code` is echoed only when it is a safe identifier (contracts/cli.md §5). */
const serverCodePattern = /^[a-z][a-z0-9_]{0,63}$/;
/** Strict RFC 3339 `date-time` (openapi); a value that is not one is refused un-echoed (S1). */
const rfc3339Pattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$/;
/** The `project_id` is a UUID (openapi `format: uuid`); a non-UUID is refused un-echoed (S1). */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The `missing_config` entry for "no exchangeable source is set" (dev cannot be exchanged). */
const exchangeSourceNames =
  `an exchange token source: ${envNames.oidcToken}, ` +
  `${envNames.actionsIdTokenRequestUrl} + ${envNames.actionsIdTokenRequestToken} or ` +
  `${envNames.ingestToken}`;

/** Remediation lines for the two binding states (04.2.1 §8 T70). */
const bindingHints: Readonly<Record<string, string>> = {
  binding_pending:
    'the repository has not presented the binding challenge yet — set the repository ' +
    'variable EVIDGATE_BINDING_CHALLENGE to the challenge shown when the binding was created',
  binding_conflict:
    'this repository is already bound to another organization; its admin must disable that ' +
    'binding first',
};

/**
 * Exchanges the configured credential for a short-lived `ci_producer` token. A 429 is
 * retried once after `Retry-After`; 401/403 raise `ExchangeAuthError`; every other
 * failure raises a `CliError` (contracts/cli.md §5).
 */
export async function exchangeToken(
  config: ResolvedConfig,
  deps: ExchangeDeps = {},
): Promise<ExchangedToken> {
  const source = resolveTokenSource(config);
  if (source === undefined || source === 'dev') {
    throw new CliError(`missing configuration: ${exchangeSourceNames}`, { code: 'missing_config' });
  }
  const fetchImpl = deps.fetch ?? fetch;
  const sleep = deps.sleep ?? ((ms: number) => delay(ms));
  const credential = await resolveCredential(config, source, fetchImpl);
  const client = createApiClient(config, credential.bearer, fetchImpl);
  const body = buildBody(config, source, credential);

  let retried = false;
  for (;;) {
    const result = await exchangeCiToken({
      client,
      body,
      signal: AbortSignal.timeout(requestTimeoutMs),
    });
    const response = result.response;
    if (response === undefined) {
      // A network error, a timeout or a refused redirect: the SDK returns no response.
      throw transportError(result.error);
    }
    if (response.ok) {
      return readSuccess(result.data, source);
    }
    if (response.status === 429 && !retried) {
      retried = true;
      await sleep(retryAfterMs(response.headers.get('retry-after')));
      continue;
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
}

/**
 * Exchanges the credential and, when the server answers 401/403, purges the cached
 * entry first so the next run re-authenticates cleanly (04.2.1 §6). A purge failure is
 * swallowed: the server's error is the one that must surface (C2).
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
  return { bearer: await fetchActionsIdToken(config, fetchImpl), ingestToken: undefined };
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
 * Fetches a GitHub Actions ID token from `ACTIONS_ID_TOKEN_REQUEST_URL` with the
 * `evidgate` audience. The URL must be `https:` (or `http:` on a loopback host); a
 * non-2xx reply or a body without a `value` is `oidc_request_failed`, a transport
 * failure is `transport` (04.2.1 §5).
 */
async function fetchActionsIdToken(
  config: ResolvedConfig,
  fetchImpl: typeof fetch,
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

  let response: Response;
  try {
    response = await fetchImpl(url, {
      headers: { Authorization: `bearer ${requestToken}` },
      redirect: 'error',
      signal: AbortSignal.timeout(requestTimeoutMs),
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

/** Validates the 200 body; the token is registered before validation (T78). */
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

/** Reads the 00 §9.2 envelope; an unusable body becomes `exchange_failed` (never echoed). */
function readEnvelope(error: unknown, response: Response): EnvelopeInfo {
  const headerRequestId = response.headers.get('x-request-id') ?? undefined;
  if (!isErrorEnvelope(error)) {
    return {
      message: 'the token exchange failed',
      options:
        headerRequestId === undefined
          ? { code: 'exchange_failed' }
          : { code: 'exchange_failed', requestId: headerRequestId },
    };
  }
  const body = error.error;
  const code = serverCodePattern.test(body.code) ? body.code : 'exchange_failed';
  const requestId = body.request_id.length > 0 ? body.request_id : headerRequestId;
  const hint = bindingHints[code];
  const escaped = escapeControlCharacters(body.message);
  return {
    message: escaped.length > maxMessageLength ? escaped.slice(0, maxMessageLength) : escaped,
    options: {
      code,
      ...(hint !== undefined ? { hint } : {}),
      ...(requestId !== undefined ? { requestId } : {}),
    },
  };
}

/** `Retry-After` as delta-seconds or an HTTP-date; missing/invalid → 1 s, clamped 0..60 s. */
function retryAfterMs(header: string | null): number {
  const value = header?.trim();
  if (value === undefined || value.length === 0) {
    return defaultRetryAfterMs;
  }
  if (/^\d+$/.test(value)) {
    return Math.min(Math.max(Number(value) * 1000, 0), maxRetryAfterMs);
  }
  // Only an IMF-fixdate (`Sun, 06 Nov 1994 08:49:37 GMT`) is parsed as an HTTP-date; a
  // malformed delta such as `1.5` or `-5`, or other text, falls back to the default (D6).
  if (/^[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT$/.test(value)) {
    const date = Date.parse(value);
    if (Number.isFinite(date)) {
      return Math.min(Math.max(date - Date.now(), 0), maxRetryAfterMs);
    }
  }
  return defaultRetryAfterMs;
}

/** A transport failure is reported with a fixed message; the cause may echo a URL or token. */
function transportError(cause: unknown): CliError {
  const timedOut =
    cause instanceof Error && (cause.name === 'AbortError' || cause.name === 'TimeoutError');
  return new CliError(timedOut ? 'the request timed out' : 'the request failed', {
    code: 'transport',
  });
}

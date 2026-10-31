// `evidgate gate` reads one run's decision and turns it into the process exit code a CI step
// branches on: only a literal PASS exits 0. `--wait` polls to one deadline, and nothing here
// may turn an unreadable answer, a refused credential or a refused redirect into a pass.
import { getRunDecision, type Client } from '@evidgate/sdk';

import { ExchangeAuthError } from '../auth/exchange.js';
import { getToken, type GetTokenDeps } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { callApi, type ApiResult } from '../callApi.js';
import { createApiClient } from '../client.js';
import type { ResolvedConfig } from '../config.js';
import { parseDuration } from '../duration.js';
import { CliError, prefixStderrLines } from '../errors.js';
import { isRetryable, retryAfter } from '../sender.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';

import { flagValue, requiredFlag, uuidPattern } from './eventsSendArgs.js';
import { usageError } from './eventsSendErrors.js';
import {
  exitCodeForDecision,
  jsonLine,
  parseDecisionText,
  reasonLines,
  verdictLine,
} from './gateDecision.js';
import { pollDecision, type PollStep } from './gatePoll.js';

import type { CommandDefinition } from './index.js';

/** The two members the SDK result is read through; `data` stays `unknown` (parsed by us). */
type DecisionResult = ApiResult & { readonly data?: unknown };

/**
 * `--wait` may not exceed this. It is the command's own ceiling, not the send deadline: a
 * gate poll and an event send are separate budgets and neither may silently cap the other.
 */
const maxWaitMs = 24 * 60 * 60_000;
/** A decision body larger than this is a contract violation, never a verdict to report. */
const maxDecisionBytes = 256 * 1024;
/**
 * Only this much of a non-2xx body is read. The envelope that carries the server's message is
 * a few hundred bytes, so anything longer is not one, and the client's own read has no limit.
 */
const maxErrorBodyBytes = 64 * 1024;
/** The status a refused redirect is reported with; `callApi` replays no 3xx and no error. */
const refusedRedirectStatus = 302;

interface DecisionRequest {
  readonly config: ResolvedConfig;
  readonly runId: string;
  readonly deps: GetTokenDeps;
  readonly fetchImpl: typeof fetch;
}

/** The bearer one `--wait` started with; a long wait may outlive it, so it is refreshed once. */
interface Session {
  client: Client;
  token: string;
  refreshed: boolean;
}

/** One poll's answer; `unauthorized` is the only failure the caller may answer with a retry. */
type DecisionAttempt = PollStep | { readonly kind: 'unauthorized'; readonly error: CliError };

export const gateCommand: CommandDefinition = {
  name: 'gate',
  summary:
    'read a run decision as an exit code (--json completeness/evaluation_id/mode/url ' +
    'are fields pending contract)',
  requireToken: true,
  options: {
    // `multiple: true` is what makes a repetition observable: with the default `false`
    // `parseArgs` keeps the last value, so `--run a --run b` would quietly ask about `b`.
    run: { type: 'string', multiple: true },
    wait: { type: 'string', multiple: true },
  },
  run: async ({ config, io, values, deps }) => {
    // Flags first, token second: an invalid flag must not reach the network.
    const runId = requiredFlag(values, 'run');
    if (!uuidPattern.test(runId)) {
      throw usageError('--run must be a UUID');
    }
    const waitMs = parseWait(flagValue(values, 'wait'));

    const { token } = await getToken(config, deps);
    const fetchImpl = deps.fetch ?? fetch;
    const session: Session = {
      client: createApiClient(config, token, transport(fetchImpl)),
      token,
      refreshed: false,
    };
    const request: DecisionRequest = { config, runId, deps, fetchImpl };
    const outcome = await pollDecision({
      waitMs,
      deps,
      next: (deadlineAt) => requestDecision(session, request, deadlineAt),
    });

    // A pending run still gets its `--json` line (with `decision: null`), so a polled step
    // can parse one shape either way; the human mode explains itself on stderr.
    if (config.json) {
      io.stdout(jsonLine(outcome));
    } else if (outcome.decision !== null) {
      io.stdout(verdictLine(outcome.decision, runId));
      const reasons = reasonLines(outcome.reasons);
      if (reasons.length > 0) {
        io.stdout(reasons);
      }
    }
    if (outcome.decision === null) {
      io.stderr(prefixStderrLines(pendingLine(runId, waitMs)));
    }
    return exitCodeForDecision(outcome.decision);
  },
};

/** `--wait`: absent means one poll; a value is the same `<n>[smh]` grammar as the deadlines. */
function parseWait(raw: string | undefined): number {
  if (raw === undefined) {
    return 0;
  }
  const parsed = parseDuration(raw, maxWaitMs);
  if (parsed.ok) {
    return parsed.milliseconds;
  }
  switch (parsed.reason) {
    case 'grammar':
      throw usageError('--wait must be an integer number of seconds, or <n>s, <n>m or <n>h');
    case 'not_positive':
      throw usageError('--wait must be greater than zero');
    case 'above_ceiling':
      throw usageError('--wait must be at most 24h');
  }
}

/**
 * One poll. A 401 is answered by minting a new token once and replaying this poll, because a
 * long wait can outlive the token it started with; the second 401 is the server's last word,
 * and a source that cannot mint a second credential never gets the replay. Both attempts are
 * bounded by the same absolute deadline, so a slow refresh cannot buy the replay a window that
 * overruns the wait.
 */
async function requestDecision(
  session: Session,
  request: DecisionRequest,
  deadlineAt: number | undefined,
): Promise<PollStep> {
  const now = request.deps.now ?? Date.now;
  const first = await attemptDecision(session, request, budgetMs(deadlineAt, now()));
  if (first.kind !== 'unauthorized') {
    return first;
  }
  if (session.refreshed) {
    throw first.error;
  }
  if (deadlineAt !== undefined && now() >= deadlineAt) {
    // The first attempt spent the wait: minting a token would only spend more of it.
    throw first.error;
  }
  session.refreshed = true;
  // Best-effort purge: the server's answer is the one that must surface.
  await deleteCachedToken(request.config).catch(() => undefined);
  const { token } = await getToken(request.config, request.deps).catch((error: unknown) => {
    if (error instanceof ExchangeAuthError) {
      // The source itself was refused — a replayed single-use JWT is the usual cause — so
      // the 401 on the decision is still the answer that explains the failure.
      throw first.error;
    }
    throw error;
  });
  if (token === session.token) {
    // The purge failed and the cache handed back the token the server just refused.
    throw first.error;
  }
  session.token = token;
  session.client = createApiClient(request.config, token, transport(request.fetchImpl));
  const refreshedAt = now();
  if (deadlineAt !== undefined && refreshedAt >= deadlineAt) {
    // The refresh spent the wait: a replay would start after the deadline, so report the
    // deadline instead of the 401 that the refresh had already answered.
    throw new CliError('the token refresh outlived the --wait deadline', {
      code: 'request_failed',
    });
  }
  const second = await attemptDecision(session, request, budgetMs(deadlineAt, refreshedAt));
  if (second.kind === 'unauthorized') {
    throw second.error;
  }
  return second;
}

/**
 * The retries one poll may spend: never past the absolute deadline, and never below the 1 ms
 * minimum the last poll needs. Without `--wait` the call keeps its own default budget.
 */
function budgetMs(deadlineAt: number | undefined, now: number): number | undefined {
  return deadlineAt === undefined ? undefined : Math.max(deadlineAt - now, 1);
}

/**
 * One request, classified for the poll loop. A body the CLI cannot read is thrown, never
 * returned as retryable: a malformed answer is a contract violation, not a slow server.
 */
async function attemptDecision(
  session: Session,
  request: DecisionRequest,
  budgetMs: number | undefined,
): Promise<DecisionAttempt> {
  const operation = (signal: AbortSignal): Promise<DecisionResult> =>
    getRunDecision({
      client: session.client,
      path: { r: request.runId },
      // The body is read below, not by the generated client, so an oversized body and a
      // stream that breaks mid-read become this command's `request_failed`.
      parseAs: 'stream',
      signal,
    });
  const result = await callApi(operation, {
    // A GET has no side effect, so a transport failure, a 429 and a 5xx may be replayed.
    policy: 'replay-safe',
    deps: request.deps,
    ...(budgetMs === undefined ? {} : { budgetMs }),
  });
  const response = result.response;
  if (response === undefined) {
    return { kind: 'transient', error: transportError(result.error) };
  }
  if (response.status === refusedRedirectStatus) {
    throw new CliError('the decision request was answered with a redirect, which is refused', {
      code: 'request_failed',
    });
  }
  const error = (): CliError =>
    serverErrorFromResponse(result.error, response, {
      code: 'request_failed',
      message: `the run decision request failed (HTTP ${response.status})`,
    });
  if (response.status === 401) {
    return { kind: 'unauthorized', error: error() };
  }
  if (!response.ok) {
    // Only the answers `callApi` itself calls transient may be polled again; a permanent
    // 4xx (403, 404, 422) fails the build at once, and so does a 5xx the envelope marks
    // `retryable: false`.
    const transient =
      (response.status === 429 || (response.status >= 500 && response.status <= 599)) &&
      isRetryable(response.status, result.error);
    if (!transient) {
      throw error();
    }
    const failure = error();
    const named = retryAfter(response, (request.deps.now ?? Date.now)(), { strict: true });
    return named === undefined
      ? { kind: 'transient', error: failure }
      : { kind: 'transient', error: failure, retryAfterMs: named };
  }
  return { kind: 'body', outcome: parseDecisionText(await readDecisionText(response)) };
}

/**
 * Reads the decision body under a hard cap. An answer larger than `maxDecisionBytes`, or one
 * whose stream breaks after its headers, is refused: a verdict the CLI only half read is never
 * a verdict, so neither case may end as a pass.
 */
async function readDecisionText(response: Response): Promise<string> {
  const body = response.body;
  if (body === null) {
    return '';
  }
  // `response.body` is typed as a stream of `any`; the reader is what fixes the chunk type,
  // so a byte count cannot silently become `NaN`.
  const reader: ReadableStreamDefaultReader<Uint8Array> = body.getReader();
  const decoder = new TextDecoder();
  let text = '';
  let bytes = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      if (!(value instanceof Uint8Array)) {
        // A non-byte chunk (only an injected fetch can produce one) would leave the cap
        // unsized, so the body is refused rather than read without a bound.
        throw new CliError('the run decision body could not be read', { code: 'request_failed' });
      }
      bytes += value.byteLength;
      if (bytes > maxDecisionBytes) {
        throw new CliError(`the run decision body exceeded ${maxDecisionBytes} bytes`, {
          code: 'request_failed',
        });
      }
      text += decoder.decode(value, { stream: true });
    }
    return text + decoder.decode();
  } catch (error) {
    if (error instanceof CliError) {
      throw error;
    }
    throw new CliError('the run decision body could not be read', { code: 'request_failed' });
  } finally {
    await reader.cancel().catch(() => undefined);
  }
}

/**
 * The fetch the SDK talks to. A refused redirect comes back as a final 3xx instead of a
 * rejection, and a non-2xx body is cut at `maxErrorBodyBytes` because the generated client
 * reads that body whole with no limit of its own.
 */
function transport(fetchImpl: typeof fetch): typeof fetch {
  const guarded = refuseRedirects(fetchImpl);
  return async (input, init) => boundedErrorBody(await guarded(input, init));
}

/**
 * Truncates an error answer's stream: past the cap the envelope cannot be parsed anyway, and
 * the caller falls back to its fixed message, so nothing readable is lost.
 */
function boundedErrorBody(response: Response): Response {
  const body = response.body;
  if (response.ok || body === null) {
    return response;
  }
  // `response.body` is typed as a stream of `any`; the reader is what fixes the chunk type,
  // so a byte count cannot silently become `NaN`.
  const reader: ReadableStreamDefaultReader<Uint8Array> = body.getReader();
  let bytes = 0;
  const capped = new ReadableStream<Uint8Array>({
    async pull(controller) {
      const { done, value } = await reader.read();
      if (done) {
        controller.close();
        return;
      }
      bytes += value.byteLength;
      if (bytes > maxErrorBodyBytes) {
        await reader.cancel().catch(() => undefined);
        controller.close();
        return;
      }
      controller.enqueue(value);
    },
    cancel: (reason: unknown) => {
      void reader.cancel(reason).catch(() => undefined);
    },
  });
  return new Response(capped, {
    status: response.status,
    statusText: response.statusText,
    headers: response.headers,
  });
}

/**
 * Reports a refused redirect as a final 3xx answer. Node rejects `redirect: 'error'` with a
 * `fetch failed` TypeError whose cause names the redirect; the target was never contacted, so
 * replaying the request would only repeat the refusal. Any other rejection stays a transport
 * failure, which `callApi` may retry.
 */
function refuseRedirects(fetchImpl: typeof fetch): typeof fetch {
  return async (input, init) => {
    try {
      return await fetchImpl(input, init);
    } catch (error) {
      if (isRefusedRedirect(error)) {
        return new Response(null, { status: refusedRedirectStatus });
      }
      throw error;
    }
  };
}

function isRefusedRedirect(error: unknown): boolean {
  return (
    error instanceof TypeError &&
    error.message === 'fetch failed' &&
    error.cause instanceof Error &&
    error.cause.message === 'unexpected redirect'
  );
}

/** The pending note on stderr: no request was repeated without `--wait`, so say which. */
function pendingLine(runId: string, waitMs: number): string {
  if (waitMs <= 0) {
    return `gate: run ${runId} has no decision yet (pending)\n`;
  }
  return `gate: run ${runId} is still pending after ${waitMs / 1000}s\n`;
}

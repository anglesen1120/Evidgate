// `evidgate gate` reads one run's decision and turns it into the process exit code a CI step
// branches on: only a literal PASS exits 0, so an unreadable body, a refused credential or a
// run that never decides can never pass a build. `--wait` polls to one deadline instead of
// asking once. `contracts/openapi.yaml` declares only `decision`, `evaluation_revision` and
// `reasons` today; the other `--json` members are printed when the server sends them and are
// `null` otherwise.
import { getRunDecision, type Client } from '@evidgate/sdk';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { callApi, type ApiResult } from '../callApi.js';
import { createApiClient } from '../client.js';
import type { ResolvedConfig } from '../config.js';
import { maxSendDeadlineMs } from '../configSpool.js';
import { parseDuration } from '../duration.js';
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
import { pollDecision, type PollDeps, type PollStep } from './gatePoll.js';

import type { CommandDefinition } from './index.js';

/** The two members the SDK result is read through; `data` stays `unknown` (parsed by us). */
type DecisionResult = ApiResult & { readonly data?: unknown };

interface DecisionRequest {
  readonly client: Client;
  readonly config: ResolvedConfig;
  readonly runId: string;
  readonly deps: PollDeps;
}

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
    const client = createApiClient(config, token, deps.fetch ?? fetch);
    const request: DecisionRequest = { client, config, runId, deps };
    const outcome = await pollDecision({
      waitMs,
      deps,
      next: (budgetMs) => requestDecision(request, budgetMs),
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
      io.stderr(pendingLine(runId, waitMs));
    }
    return exitCodeForDecision(outcome.decision);
  },
};

/** `--wait`: absent means one request; a value is the same `<n>[smh]` grammar as the deadlines. */
function parseWait(raw: string | undefined): number {
  if (raw === undefined) {
    return 0;
  }
  const parsed = parseDuration(raw, maxSendDeadlineMs);
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
 * One request, classified for the poll loop. A body the CLI cannot read is thrown, never
 * returned as retryable: a malformed answer is a contract violation, not a slow server.
 */
async function requestDecision(
  request: DecisionRequest,
  budgetMs: number | undefined,
): Promise<PollStep> {
  const operation = (signal: AbortSignal): Promise<DecisionResult> =>
    getRunDecision({
      client: request.client,
      path: { r: request.runId },
      // The body is parsed here, not by the generated client, so malformed JSON and an
      // unknown `decision` become this command's `request_failed` and not a thrown syntax error.
      parseAs: 'text',
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
  if (!response.ok) {
    if (response.status === 401) {
      // Best-effort: the server's error is the one that must surface.
      await deleteCachedToken(request.config).catch(() => undefined);
    }
    const error = serverErrorFromResponse(result.error, response, {
      code: 'request_failed',
      message: `the run decision request failed (HTTP ${response.status})`,
    });
    // Only the answers `callApi` itself calls transient may be polled again; a permanent
    // 4xx (401, 403, 404, 422) fails the build at once, and so does a 5xx the envelope
    // marks `retryable: false`.
    const transient =
      (response.status === 429 || (response.status >= 500 && response.status <= 599)) &&
      isRetryable(response.status, result.error);
    if (!transient) {
      throw error;
    }
    const named = retryAfter(response, (request.deps.now ?? Date.now)(), { strict: true });
    return named === undefined
      ? { kind: 'transient', error }
      : { kind: 'transient', error, retryAfterMs: named };
  }
  if (result.error !== undefined) {
    // A 2xx whose body could not be read (an aborted or reset stream) is an incomplete
    // answer: poll again instead of reporting a verdict the CLI never read.
    return { kind: 'transient', error: transportError(result.error) };
  }
  return { kind: 'body', outcome: parseDecisionText(result.data) };
}

/** The pending note on stderr: no request was repeated without `--wait`, so say which. */
function pendingLine(runId: string, waitMs: number): string {
  if (waitMs <= 0) {
    return `gate: run ${runId} has no decision yet (pending)\n`;
  }
  return `gate: run ${runId} is still pending after ${waitMs / 1000}s\n`;
}

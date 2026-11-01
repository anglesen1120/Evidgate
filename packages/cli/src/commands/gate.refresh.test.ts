// `evidgate gate --wait` can outlive the bearer it started with more than once. Each expiry
// gets its own refresh, so a wait that spans three token lifetimes must still reach the
// decision, and a 401 on a token the server never accepted must still end the poll at once.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { exchangeBody } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  gateArgv,
  ingestEnv,
  invoke,
  jsonResponse,
  refusal,
  runId,
  stubFetch,
} from './gate.kit.test.js';

import type { StubCall } from './gate.kit.test.js';

const exchangePath = '/v1/ci/token';
const decisionPath = `/v1/runs/${runId}/decision`;

interface TokenStub {
  readonly fetchImpl: typeof fetch;
  /** How many `/v1/ci/token` exchanges the command asked for. */
  readonly exchanges: () => number;
  /** Only the decision polls, so the exchange traffic cannot mask a missing poll. */
  readonly decisionCalls: () => readonly StubCall[];
}

/** Answers every exchange with a distinct bearer and hands the polls to `answer`. */
function tokenStub(answer: (authorization: string | null) => Response): TokenStub {
  let exchanges = 0;
  const stub = stubFetch((request) => {
    if (request.url.endsWith(exchangePath)) {
      exchanges += 1;
      return jsonResponse(200, exchangeBody(`egt-token-${exchanges}`));
    }
    return answer(request.headers.get('authorization'));
  });
  return {
    fetchImpl: stub.fetchImpl,
    exchanges: () => exchanges,
    decisionCalls: () => stub.calls.filter((call) => !call.request.url.endsWith(exchangePath)),
  };
}

describe('gate --wait across token lifetimes', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('refreshes once per expiry and reaches the decision after three lifetimes', async () => {
    const env = await ingestEnv(dirs);
    // Each minted token answers one pending poll and is refused on the next use, so every 401
    // that follows an accepted answer is a new expiry rather than a refusal of a fresh token.
    // The wait also starts on a token that has already expired.
    const used = new Map<string, number>();
    const stub = tokenStub((authorization) => {
      const bearer = authorization ?? '';
      if (bearer === 'Bearer egt-token-4') {
        return decided({ decision: 'PASS' });
      }
      const polls = (used.get(bearer) ?? 0) + 1;
      used.set(bearer, polls);
      return bearer === 'Bearer egt-token-1' || polls > 1
        ? refusal(401, 'token_invalid')
        : decided({ decision: null });
    });
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '1m']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    // The initial mint plus one refresh per expiry: three lifetimes spent inside the one wait.
    expect(stub.exchanges()).toBe(4);
    // Each bearer serves the poll that expired and the replay its refresh bought; the fourth
    // is the one that finally sees the decision.
    expect(
      stub.decisionCalls().map((call) => call.request.headers.get('authorization')),
    ).toStrictEqual([
      'Bearer egt-token-1',
      'Bearer egt-token-2',
      'Bearer egt-token-2',
      'Bearer egt-token-3',
      'Bearer egt-token-3',
      'Bearer egt-token-4',
    ]);
    expect(clock.sleeps).toStrictEqual([1_000, 1_000]);
    expect(out.stdout.join('')).toBe(`gate: PASS (run ${runId})\n`);
    expect(out.stderr.join('')).toBe('');
  });

  it('exits 3 when a refreshed token is refused again before any answer accepts it', async () => {
    const env = await ingestEnv(dirs);
    // Every poll is refused, so the replay after the first refresh is the server's last word:
    // one refresh, no second expiry to spend, and no loop even with a minute of wait left.
    const stub = tokenStub(() => refusal(401, 'token_invalid'));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '1m']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.exchanges()).toBe(2);
    expect(clock.sleeps).toStrictEqual([]);
    expect(
      stub.decisionCalls().map((call) => call.request.headers.get('authorization')),
    ).toStrictEqual(['Bearer egt-token-1', 'Bearer egt-token-2']);
    expect(stub.decisionCalls().map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      decisionPath,
      decisionPath,
    ]);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('error token_invalid');
  });
});

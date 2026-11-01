// The dev-token exchange seam the `evidgate` command specs share. `EVIDGATE_DEV_TOKEN` is an
// ingest credential, so `getToken` exchanges it at `POST /v1/ci/token` and the command's own
// requests carry the minted `ci_producer` bearer; every spec that drives a dev-token run
// answers that exchange with this stub instead of carrying its own copy.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist` — the
// no-emit tsconfig.spec.json is the project that type-checks it.
/** A valid `project_id`, as a token-exchange answer carries it. */
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';
/** The `EVIDGATE_DEV_TOKEN` value: an ingest credential the exchange presents, never a bearer. */
export const devToken = 'dev-token-value';
/**
 * The `ci_producer` token the dev exchange mints: the bearer a command sends. It is
 * deliberately a different string from `devToken`, so a command that sent the dev token raw
 * would fail every `Bearer ${mintedToken}` assertion.
 */
export const mintedToken = 'egt_minted_ci_token';
/** The exchange route a dev run posts to before its own request. */
const exchangePath = '/v1/ci/token';

/** One recorded request: the `Request` and the raw bytes it carried. */
export interface DevStubCall {
  readonly request: Request;
  readonly body: Buffer;
}

/** The stub's transport and the two request lists it keeps apart. */
export interface DevStub {
  readonly fetchImpl: typeof fetch;
  /** The command's own requests; the dev token's exchange is never one of them. */
  readonly calls: DevStubCall[];
  /** The `POST /v1/ci/token` requests the command made, in order. */
  readonly exchanges: DevStubCall[];
}

/**
 * The fixed expiry most exchange answers carry. It is deliberately beyond the cache's
 * `maxTokenLifetimeMs`, so a spec that does not ask for a cacheable token sees every
 * `getToken()` exchange instead of a cache hit.
 */
const uncacheableExpiresAt = '2030-01-01T00:00:00Z';

/** The parts of the answer a spec may need to differ on; the rest are the common answer. */
export interface ExchangeAnswer {
  /** The bearer handed out; `mintedToken` unless a spec mints its own. */
  readonly token?: string;
  /** The `project_id` echoed back (openapi `format: uuid`). */
  readonly projectId?: string;
  /** ISO-8601 expiry; a token inside `maxTokenLifetimeMs` is cached and reused. */
  readonly expiresAt?: string;
  /** The `ci_producer` permission set the answer carries. */
  readonly permissionSet?: ReadonlyArray<string>;
}

/** The `/v1/ci/token` answer that hands out `token` (openapi `CiTokenResponse`). */
export function exchangeBody(token: string, answer: Omit<ExchangeAnswer, 'token'> = {}): unknown {
  return {
    token,
    expires_at: answer.expiresAt ?? uncacheableExpiresAt,
    project_id: answer.projectId ?? projectId,
    permission_set: answer.permissionSet ?? [],
  };
}

/**
 * Whether `request` is the dev exchange. A `Request` and a spec's own recorded call (any
 * `{url, method}`) both qualify, so a stub that already built its own call object asks here
 * instead of matching the route a second time.
 */
export function isDevExchange(request: { readonly url: string; readonly method: string }): boolean {
  return request.method === 'POST' && new URL(request.url).pathname === exchangePath;
}

/** The exchange answer for `request`, or `undefined` when it is the command's own request. */
export function devExchangeAnswer(
  request: { readonly url: string; readonly method: string },
  answer: ExchangeAnswer = {},
): Response | undefined {
  if (!isDevExchange(request)) {
    return undefined;
  }
  return new Response(JSON.stringify(exchangeBody(answer.token ?? mintedToken, answer)), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

/**
 * `fetchImpl` with the dev exchange answered in front of it, so the wrapped transport sees only
 * the requests the command itself sends. The answer is `mintedToken` by default, never
 * `devToken`: an ingest credential is exchanged, not sent.
 */
export function withTokenExchange(
  fetchImpl: typeof fetch,
  options: ExchangeAnswer = {},
): typeof fetch {
  return (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const exchange = devExchangeAnswer(request, options);
    return exchange === undefined ? fetchImpl(input, init) : Promise.resolve(exchange);
  };
}

/**
 * A `fetch` stub for a dev-token run: the exchange is answered with `token` and every other
 * request goes to `handler`, so `index` counts the command's own requests and `calls` never
 * holds the exchange — an empty `calls` therefore means the command sent nothing.
 */
export function devStubFetch(
  handler: (request: Request, index: number) => Response | Promise<Response>,
  token: string = mintedToken,
): DevStub {
  const calls: DevStubCall[] = [];
  const exchanges: DevStubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const call: DevStubCall = {
      request,
      body: Buffer.from(await request.clone().arrayBuffer()),
    };
    const exchange = devExchangeAnswer(request, { token });
    if (exchange !== undefined) {
      exchanges.push(call);
      return exchange;
    }
    calls.push(call);
    return handler(request, calls.length - 1);
  };
  return { fetchImpl, calls, exchanges };
}

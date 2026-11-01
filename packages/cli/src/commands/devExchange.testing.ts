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

/** The `/v1/ci/token` answer that hands out `token` (openapi `CiTokenResponse`). */
export function exchangeBody(token: string): unknown {
  return { token, expires_at: '2030-01-01T00:00:00Z', project_id: projectId, permission_set: [] };
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
    if (request.method === 'POST' && new URL(request.url).pathname === exchangePath) {
      exchanges.push(call);
      return new Response(JSON.stringify(exchangeBody(token)), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      });
    }
    calls.push(call);
    return handler(request, calls.length - 1);
  };
  return { fetchImpl, calls, exchanges };
}
